using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.AdoptPositionDrift;
using OrderExecutionService.Features.OrderExecution.AmendOrder;
using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-641・T-10-642・T-10-739, FR-10, FR-05, FR-11, UC-06, #858, IADR-0370, IADR-0350 決定5:
// **発注執行が PositionDriftAdopted を購読していること**（サービス間は直接参照しない）と、
// 追随の結果が実際に発行されることを、本番のハンドラ型そのもので固定する。
//
// 購読が配線されていなければ、乖離の取り込みは取引台帳だけを動かし、
// ブローカーには**建玉なき逆指値**が残り続ける（発火すると意図しないショート）。
public class PositionDriftAdoptedHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 7, 0, 0, TimeSpan.Zero);
    private const string ServiceName = "ai-stock-trading.order-execution-service";

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // 建玉は空（取り込みの観測どおり消えている）。取消後の照会が返す状態だけ注入する。
    private sealed class ScriptedBroker(OrderStatus? afterCancel, bool positionsUnknown = false)
        : IBrokerAdapter, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public int CancelCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException("本テストは発注しない");

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(afterCancel is { } status
                ? new BrokerOrder(orderId, Intent(), status, 0, 0m, Now, Now)
                : null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            CancelCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BrokerPositionSnapshot>?>(positionsUnknown ? null : []);
    }

    private static OrderIntent Intent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
            10, 950m, PositionEffect.Close);

    private static Task<IHost> BuildHostAsync(
        IBrokerAdapter broker, InMemoryExecutedOrderStore executedOrders, InMemoryProtectiveStopOrderStore stops) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, FakeClock>();
                opts.Services.AddSingleton(broker);
                opts.Services.AddSingleton<IExecutedOrderStore>(executedOrders);
                opts.Services.AddSingleton<IProtectiveStopOrderStore>(stops);
                opts.Services.AddSingleton<IOrderLifecycleStore, InMemoryOrderLifecycleStore>();
                opts.Services.AddSingleton<IBrokerPositionSource>((IBrokerPositionSource)broker);
                opts.Services.AddScoped<OrderAmendmentService>();
                opts.Services.AddScoped<ProtectiveStopDriftAdopter>();

                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<PositionDriftAdoptedHandler>();
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static ProtectiveStopOrder SeedStop(
        InMemoryProtectiveStopOrderStore stops, InMemoryExecutedOrderStore store)
    {
        var entryDecisionId = Guid.NewGuid();
        var stopDecisionId = ProtectiveStopIds.StopDecisionId(entryDecisionId, attempt: 1);
        var stop = new ProtectiveStopOrder(
            entryDecisionId, stopDecisionId, "stop-1", "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, Attempt: 1,
            ProtectiveStopState.Active, Now.AddMinutes(-30), Now.AddMinutes(-30), RemainingProtected: 10);
        stops.Save(stop);
        store.Save(new ExecutionRecord(
            stopDecisionId, "stop-1", "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            PositionEffect.Close, 10, 950m, 0, 0m, OrderStatus.Accepted, 0m, Now.AddMinutes(-30)));
        return stop;
    }

    private static PositionDriftAdopted Adopted() =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, 10, 0, 0, Now.AddMinutes(-5), 1_000m,
            RealizedPnlRecorded: false, ReferencePrice: null, EstimatedPnlInBase: null,
            Actor: "owner", Reason: "証券会社のアプリで全株売却", AdoptedAt: Now);

    [Fact]
    public async Task 取り込みを購読して保護レグを取り消し_取消と保護減少を発行する()
    {
        var broker = new ScriptedBroker(OrderStatus.Cancelled);
        var executedOrders = new InMemoryExecutedOrderStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        using var host = await BuildHostAsync(broker, executedOrders, stops);
        var stop = SeedStop(stops, executedOrders);

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Adopted());

        broker.CancelCount.Should().Be(1, "購読が無ければ建玉なき逆指値がブローカーに残り続ける");
        session.Sent.MessagesOf<OrderCancelled>().Should().ContainSingle()
            .Which.DecisionId.Should().Be(stop.StopDecisionId);
        session.Sent.MessagesOf<SoftwareStopExecuted>().Should().ContainSingle()
            .Which.Outcome.Should().Be(SoftwareStopOutcome.ProtectionReduced);
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // 🔴 否定形: 取り消せたと確認できないとき、ハンドラは**例外を投げずに** Critical を発行し、記録を Active のまま残す。
    // 投げると成功した行の処理まで捨てて再配送になる（追随は冪等なので壊れはしないが、可視化は
    // 「Active のまま」「Critical」「ガードの巡回」の 3 つで足りる。IADR-0370 決定5）。
    [Fact]
    public async Task 取消を確認できなくても例外を投げずCriticalを発行し記録はActiveのまま()
    {
        var broker = new ScriptedBroker(afterCancel: null);
        var executedOrders = new InMemoryExecutedOrderStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        using var host = await BuildHostAsync(broker, executedOrders, stops);
        var stop = SeedStop(stops, executedOrders);

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Adopted());

        broker.CancelCount.Should().Be(1);
        session.Sent.MessagesOf<OrderCancelled>().Should().BeEmpty("確認できないまま在庫の押さえを解かない");
        session.Sent.MessagesOf<SoftwareStopExecuted>().Should().ContainSingle()
            .Which.Outcome.Should().Be(SoftwareStopOutcome.StopCancelUnconfirmed);
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }

    // ---- T-10-739: 建玉照会が不明なら、ハンドラは例外を投げて再試行へ回し、何も発行しない ----
    // 🔴 PR #918 の監査（IADR-0370 2026-09-24 追記）: 例外を握って正常終了すると、メッセージは消費され
    // 「建玉が確かめられたら取り消す」機会が失われる。投げれば Wolverine の再試行（2s/10s/30s → _error）が照会をやり直す。
    [Fact]
    public async Task 建玉照会が不明なら例外を投げて再試行へ回し_取消も発行もしない_否定形()
    {
        var broker = new ScriptedBroker(OrderStatus.Cancelled, positionsUnknown: true);
        var executedOrders = new InMemoryExecutedOrderStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        using var host = await BuildHostAsync(broker, executedOrders, stops);
        var stop = SeedStop(stops, executedOrders);

        // ハンドラの例外が呼び出し側（受信経路では共通の再試行ポリシー。IADR-0129 決定 5）へ届くこと。
        // 🔴 Wolverine の受信経路へ流すと、再試行の待ち（2s/10s/30s）を壁時計で待つことになる。
        // ここで固定するのは「ハンドラが握らずに投げる」ことであり、再試行ポリシーは全ハンドラ共通の配線が持つ。
        // 依存は本番と同じ DI の組み立てから取り出す（発行先も Wolverine の本物の IMessageBus）。
        using var scope = host.Services.CreateScope();
        var handler = new PositionDriftAdoptedHandler(
            scope.ServiceProvider.GetRequiredService<ProtectiveStopDriftAdopter>(),
            NullLogger<PositionDriftAdoptedHandler>.Instance);
        await Assert.ThrowsAsync<ProtectiveStopDriftPositionsUnknownException>(() =>
            handler.Handle(Adopted(), scope.ServiceProvider.GetRequiredService<IMessageBus>(), CancellationToken.None));

        broker.CancelCount.Should().Be(0, "建玉が消えたと確かめられないまま保護を取り消さない");
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
        stops.Find(stop.EntryDecisionId)!.RemainingProtected.Should().Be(10);
    }
}
