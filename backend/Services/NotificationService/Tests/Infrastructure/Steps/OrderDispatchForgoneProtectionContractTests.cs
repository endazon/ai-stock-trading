extern alias OrderExecutionWorker;

using NotificationService.Features.Notifications;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine;
using Wolverine.Runtime;
using Xunit;
using OeAppService = OrderExecutionWorker::OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;
using OeClock = OrderExecutionWorker::OrderExecutionService.Common.Abstractions.IClock;
using OeExecutedStore = OrderExecutionWorker::OrderExecutionService.Infrastructure.Persistence.InMemoryExecutedOrderStore;
using OeReservationStore = OrderExecutionWorker::OrderExecutionService.Infrastructure.Persistence.InMemoryOrderReservationStore;
using OeStopStore = OrderExecutionWorker::OrderExecutionService.Infrastructure.Persistence.InMemoryProtectiveStopOrderStore;
using OeStopOrder = OrderExecutionWorker::OrderExecutionService.Domain.ProtectiveStopOrder;
using OeStopState = OrderExecutionWorker::OrderExecutionService.Domain.ProtectiveStopState;

namespace NotificationService.Tests;

// 🔴 T-10-1007, FR-10, FR-09, UC-06, #879, IADR-0424 決定3, IADR-0420 決定1: **見送り（OrderDispatchForgone）の越境の契約**。
// 送り手（発注執行）の**本物の OrderExecutionAppService** が建玉照会の不明で見送った決済を、受け手（通知）の**本番の Program.cs が組んだ
// Wolverine の既定シリアライザ**で書いて読み、**本番のホストのハンドラ**へ流して、保護の記録が通知の本文まで届くことを固定する。
//
// 型は Shared.Contracts の共有型だが、両サービスの試験は各自で値を組むため、送り手が項目を載せ忘れても・受け手の読みが既定値へ
// 落ちても、どちらの試験も緑のまま「保護の記録を確認できませんでした」と書き続け得る（#940 / #943 と同じ形）。ここでは送り手の値を
// 送り手のコードで作り、通信路を通してから受け手に読ませる。
public class OrderDispatchForgoneProtectionContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 6, 0, 0, TimeSpan.Zero);

    private sealed class Clock : OeClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // 建玉照会が不明（null）を返す moomoo 相当のブローカー。発注されたら試験を落とす。
    private sealed class IndeterminateBroker : IBrokerAdapter, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BrokerPositionSnapshot>?>(null);

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new InvalidOperationException("照会不明の決済は送らない（IADR-0355 決定3）");

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;
    }

    // 送り手のコードで見送りを作る。stops を渡さなければ記録ストアの無い構成（Unknown）。
    private static async Task<OrderDispatchForgone> SenderForgoesCloseAsync(OeStopStore? stops)
    {
        var broker = new IndeterminateBroker();
        var service = new OeAppService(
            broker, new OeExecutedStore(), new OeReservationStore(), new Clock(), stops, null, broker);
        var intent = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 300, 100m, PositionEffect.Close);

        var result = await service.ExecuteAsync(new OrderApproved(Guid.NewGuid(), intent, intent.Quantity, Now));

        result.Forgone.Should().NotBeNull("前提: 送り手が照会不明で見送った");
        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.BrokerPositionsIndeterminate);
        return result.Forgone;
    }

    // 受け手の本番の Program.cs を組み、送信だけを記録 sender へ差し替える（外部へ送らない）。
    private static WebApplicationFactoryWithSender ReceiverHost(RecordingNotificationSender sender) => new(sender);

    private sealed class WebApplicationFactoryWithSender(RecordingNotificationSender sender) : IAsyncDisposable
    {
        private readonly NotificationWorkerWebApplicationFactory _root = new();
        private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? _factory;

        public IServiceProvider Services => (_factory ??= _root.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<INotificationSender>();
            s.AddSingleton<INotificationSender>(sender);
        }))).Services;

        public async ValueTask DisposeAsync()
        {
            if (_factory is not null)
                await _factory.DisposeAsync();
            await _root.DisposeAsync();
        }
    }

    // 通信路: 受け手の本番の Wolverine が持つ既定シリアライザで書き、同じシリアライザで読む。
    private static OrderDispatchForgone ThroughWire(IServiceProvider receiver, OrderDispatchForgone sent)
    {
        var serializer = receiver.GetRequiredService<IWolverineRuntime>().Options.DefaultSerializer;
        var bytes = serializer.WriteMessage(sent);
        return (OrderDispatchForgone)serializer.ReadFromData(typeof(OrderDispatchForgone), new Envelope { Data = bytes });
    }

    [Fact]
    public async Task 送り手が保護記録なしで見送った決済は通信路を越えて保護レグを持たない建玉と断定して通知される()
    {
        var sent = await SenderForgoesCloseAsync(new OeStopStore());
        var sender = new RecordingNotificationSender();
        await using var host = ReceiverHost(sender);

        var received = ThroughWire(host.Services, sent);
        await host.Services.GetRequiredService<IMessageBus>().InvokeAsync(received);

        received.Protection.Should().Be(new ForgoneCloseProtection(ForgoneCloseProtectionStatus.NoneRecorded, 0, 0));
        var msg = sender.Sent.Should().ContainSingle().Which;
        msg.Severity.Should().Be(NotificationSeverity.Critical);
        msg.Content.Should().Contain("保護レグを持たない建玉です").And.NotContain("可能性");
    }

    [Fact]
    public async Task 送り手が保護記録ありで見送った決済は通信路を越えて株数つきで通知される()
    {
        var stops = new OeStopStore();
        var entry = Guid.NewGuid();
        stops.Save(new OeStopOrder(
            entry, Guid.NewGuid(), "STOP-1", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 120, 95m, 1m, 1, OeStopState.Active, Now.AddHours(-1), Now.AddHours(-1)));
        var sent = await SenderForgoesCloseAsync(stops);
        var sender = new RecordingNotificationSender();
        await using var host = ReceiverHost(sender);

        var received = ThroughWire(host.Services, sent);
        await host.Services.GetRequiredService<IMessageBus>().InvokeAsync(received);

        received.Protection.Should().Be(new ForgoneCloseProtection(ForgoneCloseProtectionStatus.Recorded, 120, 0));
        sender.Sent.Should().ContainSingle().Which.Content.Should()
            .Contain("ブローカー側の保護注文（逆指値など）が 120 株分あります")
            .And.Contain("決済しようとした 300 株のうち 180 株には");
    }

    [Fact]
    public async Task 送り手が記録を読めない構成で見送った決済は通信路を越えて可能性として通知される()
    {
        var sent = await SenderForgoesCloseAsync(stops: null);
        var sender = new RecordingNotificationSender();
        await using var host = ReceiverHost(sender);

        var received = ThroughWire(host.Services, sent);
        await host.Services.GetRequiredService<IMessageBus>().InvokeAsync(received);

        received.Protection!.Status.Should().Be(ForgoneCloseProtectionStatus.Unknown);
        sender.Sent.Should().ContainSingle().Which.Content.Should().Contain("この建玉は保護レグを持たない可能性があります");
    }
}
