using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationService.Features.Notifications;
using NotificationService.Infrastructure.Steps;
using Xunit;

namespace NotificationService.Tests;

// 🔴 T-10-2650, FR-09, FR-04, #1338, IADR-0535 決定4: 「処理中の決済が建玉を覆うため決済を見送った」の通知を、同じ建玉・同じ処理中の決済
// （注文 ID）につき 1 回へ畳む。是正前は AAPL の指値の決済が約定しない間、見送りの通知が 1 セッションに 33 件出た（2026-10-13）。
// ほかの理由・注文 ID の無い見送りは従来どおり毎回通知する。監査は別のキューで全件を受け取る（ここでは触れない）。
public class InFlightCloseForgoneNotificationSuppressionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 13, 42, 0, TimeSpan.Zero);

    private static OrderDispatchForgone Forgone(
        DateTimeOffset at, IReadOnlyList<string>? orderIds = null, string symbol = "AAPL", TradeSide side = TradeSide.Sell,
        OrderDispatchForgoneReason reason = OrderDispatchForgoneReason.InFlightCloseCoversPosition) =>
        new(Guid.NewGuid(),
            new OrderIntent(symbol, Market.UnitedStates, side, ProductType.Cash, BrokerProvider.MoomooSimulate, 13, 347.93m, PositionEffect.Close),
            reason, at, InFlightCloseOrderIds: orderIds);

    private static (OrderDispatchForgoneNotificationHandler Handler, RecordingNotificationSender Sender) Create()
    {
        var sender = new RecordingNotificationSender();
        return (new OrderDispatchForgoneNotificationHandler(
            sender, new InFlightCloseForgoneNotificationSuppressor(),
            NullLogger<OrderDispatchForgoneNotificationHandler>.Instance), sender);
    }

    // 🔴 issue の形: 5.5 分ごとに 33 回、同じ処理中の決済（AAPL-SELL-1）を理由に見送られても通知は 1 件。
    [Fact]
    public async Task T_10_2650_同じ処理中の決済の見送りは1回だけ通知する()
    {
        var (handler, sender) = Create();

        for (var i = 0; i < 33; i++)
            await handler.Handle(Forgone(T0.AddMinutes(5.5 * i), ["AAPL-SELL-1"]), CancellationToken.None);

        sender.Sent.Should().ContainSingle("同じ建玉・同じ処理中の決済の見送りは 1 回に畳む");
    }

    // 処理中の決済が替われば（注文 ID の集合が違う）・銘柄や方向が違えば、別の見送りとして通知する。並びは問わない。
    [Fact]
    public async Task T_10_2650_処理中の決済や建玉が替われば改めて通知し並びは問わない()
    {
        var (handler, sender) = Create();

        await handler.Handle(Forgone(T0, ["A", "B"]), CancellationToken.None);
        await handler.Handle(Forgone(T0.AddMinutes(1), ["B", "A"]), CancellationToken.None);
        sender.Sent.Should().HaveCount(1, "注文 ID の集合が同じなら並びは問わない");

        await handler.Handle(Forgone(T0.AddMinutes(2), ["C"]), CancellationToken.None);
        sender.Sent.Should().HaveCount(2, "処理中の決済が替われば改めて通知する");

        await handler.Handle(Forgone(T0.AddMinutes(3), ["C"], symbol: "MSFT"), CancellationToken.None);
        await handler.Handle(Forgone(T0.AddMinutes(4), ["C"], side: TradeSide.Buy), CancellationToken.None);
        sender.Sent.Should().HaveCount(4, "銘柄・方向が違えば別の建玉");

        await handler.Handle(Forgone(T0 + InFlightCloseForgoneNotificationSuppressor.RetainFor, ["A", "B"]), CancellationToken.None);
        sender.Sent.Should().HaveCount(5, "記憶を捨てた後は改めて通知する");
    }

    // 🔴 陰性: 注文 ID の無い見送り（旧い送り手）・ほかの理由の見送りは畳まない（従来どおり毎回通知する）。
    [Fact]
    public async Task T_10_2650_注文IDの無い見送りやほかの理由は畳まない()
    {
        var (handler, sender) = Create();

        await handler.Handle(Forgone(T0), CancellationToken.None);
        await handler.Handle(Forgone(T0.AddMinutes(1), []), CancellationToken.None);
        await handler.Handle(Forgone(T0.AddMinutes(2), ["X"], reason: OrderDispatchForgoneReason.BrokerUnavailable), CancellationToken.None);
        await handler.Handle(Forgone(T0.AddMinutes(3), ["X"], reason: OrderDispatchForgoneReason.BrokerUnavailable), CancellationToken.None);

        sender.Sent.Should().HaveCount(4);
    }

    // 送信に失敗した見送りは記憶を戻し、例外を伝える（メッセージングの再試行で送り直す）。
    [Fact]
    public async Task T_10_2650_送信に失敗した見送りは再試行で送り直す()
    {
        var failing = new FailingOnceSender();
        var handler = new OrderDispatchForgoneNotificationHandler(
            failing, new InFlightCloseForgoneNotificationSuppressor(), NullLogger<OrderDispatchForgoneNotificationHandler>.Instance);
        var forgone = Forgone(T0, ["AAPL-SELL-1"]);

        var first = () => handler.Handle(forgone, CancellationToken.None);
        await first.Should().ThrowAsync<InvalidOperationException>();
        await handler.Handle(forgone, CancellationToken.None);

        failing.Sent.Should().ContainSingle("失敗した送信は通知済みに数えない");
    }

    // 本番の組み立ては畳み込みの記憶を singleton で持つ（メッセージごとに作り直すと何も畳まない）。
    [Fact]
    public void T_10_2650_本番の組み立ては畳み込みの記憶を1つだけ持つ()
    {
        using var factory = new NotificationWorkerWebApplicationFactory();

        var a = factory.Services.GetRequiredService<InFlightCloseForgoneNotificationSuppressor>();
        var b = factory.Services.CreateScope().ServiceProvider.GetRequiredService<InFlightCloseForgoneNotificationSuppressor>();

        a.Should().BeSameAs(b);
    }

    private sealed class FailingOnceSender : INotificationSender
    {
        private bool _failed;

        public List<NotificationMessage> Sent { get; } = [];

        public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
        {
            if (!_failed)
            {
                _failed = true;
                throw new InvalidOperationException("Discord への送信に失敗した（試験）");
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
