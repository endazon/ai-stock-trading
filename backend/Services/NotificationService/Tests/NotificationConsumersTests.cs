using NotificationService.Features.Notifications;
using NotificationService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace NotificationService.Tests;

// FR-09, UC-01, UC-02, UC-06: 取引実行・リスク統制発動のイベント購読 → 通知送信を
// Wolverine のテストハーネス（Wolverine.Tracking）+ 記録 sender で検証する。
//
// ADR-0013, IADR-0129, #354: AddMassTransitTestHarness + harness.Consumed から
// TrackActivity + session.Executed へ移行した。表明の意味は同じ
//（イベントを流し、ハンドラが実行され、期待した通知が 1 件だけ送られる）。
// Wolverine はハンドラを明示登録せずアセンブリ走査で発見するため、テスト側の購読列挙は不要になった
//（発見範囲＝本番と同じ「Infrastructure アセンブリ全体」になり、テストと本番のズレが構造的に消える）。
public class NotificationConsumersTests
{
    private static OrderIntent Intent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 1_000m);

    private static async Task<(IHost Host, RecordingNotificationSender Sender)> BuildAsync()
    {
        var sender = new RecordingNotificationSender();
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<INotificationSender>(sender);
                opts.Discovery.IncludeAssembly(typeof(OrderExecutedNotificationHandler).Assembly);
                // 実ブローカへ接続しない（ローカル・CI ともに RabbitMQ を要求しない）。
                opts.StubAllExternalTransports();
            })
            .StartAsync();
        return (host, sender);
    }

    [Fact]
    public async Task 約定イベントは取引実行通知を送信する()
    {
        var (host, sender) = await BuildAsync();
        using var _ = host;

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderExecuted(Guid.NewGuid(), "ORD-1", OrderStatus.Filled, 10, 1_050m, DateTimeOffset.UtcNow, BrokerProvider.MoomooSimulate));
        session.Executed.MessagesOf<OrderExecuted>().Should().NotBeEmpty();

        sender.Sent.Should().ContainSingle(m => m.Title == "取引実行");

        await host.StopAsync();
    }

    [Fact]
    public async Task 拒否イベントは理由つきのリスク統制通知を送信する()
    {
        var (host, sender) = await BuildAsync();
        using var _ = host;

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderRejected(
            Guid.NewGuid(), Intent(), new[] { RejectionReason.KillSwitchActive }, DateTimeOffset.UtcNow));
        session.Executed.MessagesOf<OrderRejected>().Should().NotBeEmpty();

        sender.Sent.Should().ContainSingle(m => m.Content.Contains(nameof(RejectionReason.KillSwitchActive)));

        await host.StopAsync();
    }

    [Fact]
    public async Task 前提条件変更イベントは設定変更通知を送信する()
    {
        var (host, sender) = await BuildAsync();
        using var _ = host;

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new AssumptionsChanged(2, "owner", "税率見直し", DateTimeOffset.UtcNow));
        session.Executed.MessagesOf<AssumptionsChanged>().Should().NotBeEmpty();

        sender.Sent.Should().ContainSingle(m => m.Title.Contains("設定変更"));

        await host.StopAsync();
    }

    [Fact]
    public async Task 報告書確定イベントは確定通知を送信する()
    {
        var (host, sender) = await BuildAsync();
        using var _ = host;

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new ReportConfirmed("daily-2026-07-10", "Daily", "owner", 1, DateTimeOffset.UtcNow));
        session.Executed.MessagesOf<ReportConfirmed>().Should().NotBeEmpty();

        sender.Sent.Should().ContainSingle(m => m.Title.Contains("報告書確定") && m.Content.Contains("owner"));

        await host.StopAsync();
    }

    [Fact]
    public async Task 費用しきい値到達イベントは費用統制通知を送信する()
    {
        var (host, sender) = await BuildAsync();
        using var _ = host;

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new CostThresholdReached("2026-07", "Llm", 100m, "Halted", DateTimeOffset.UtcNow));
        session.Executed.MessagesOf<CostThresholdReached>().Should().NotBeEmpty();

        sender.Sent.Should().ContainSingle(m => m.Title.Contains("費用統制") && m.Severity == NotificationSeverity.Critical);

        await host.StopAsync();
    }

    [Fact]
    public async Task 損切りイベントは_Critical_通知を送信する()
    {
        var (host, sender) = await BuildAsync();
        using var _ = host;

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new StopLossTriggered(
            Guid.NewGuid(), "7203", Market.Japan, TradeSide.Buy, 5, 950m, 940m, DateTimeOffset.UtcNow));
        session.Executed.MessagesOf<StopLossTriggered>().Should().NotBeEmpty();

        sender.Sent.Should().ContainSingle(m => m.Severity == NotificationSeverity.Critical);

        await host.StopAsync();
    }

    [Fact]
    public async Task 撤退基準到達イベントは自動停止つきで_Critical_通知を送信する()
    {
        // FR-20, FR-09, #166: 撤退基準到達（自動停止）の通知。降格提案は「確定は承認が必要」を本文で明示する。
        var (host, sender) = await BuildAsync();
        using var _ = host;

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new WithdrawalTriggered(
            ProposedStage: 0, "DrawdownBreachedMultiple", HaltNewEntries: true, DateTimeOffset.UtcNow));
        session.Executed.MessagesOf<WithdrawalTriggered>().Should().NotBeEmpty();

        sender.Sent.Should().ContainSingle(m =>
            m.Title.Contains("撤退基準到達")
            && m.Severity == NotificationSeverity.Critical
            && m.Content.Contains("自動停止")
            && m.Content.Contains("承認"));

        await host.StopAsync();
    }

    // --- #381 停止側 / IADR-0198 決定1・決定3 ---------------------------------------------------

    // 🔴 **統制の発動が日々の警告に埋もれないこと。** 同じイベント型を使う以上、
    // **重大度と件名で読み分けられなければ決定1 は成立しない。**
    [Fact]
    public async Task 鮮度切れは_Critical_で新規建ての停止を通知する()
    {
        var (host, sender) = await BuildAsync();
        using var _ = host;

        var now = DateTimeOffset.UtcNow;
        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new FxRateStale("USD", now.AddDays(-31), 31, 5, 30, now, EntryBlocked: true));
        session.Executed.MessagesOf<FxRateStale>().Should().NotBeEmpty();

        sender.Sent.Should().ContainSingle(m =>
            m.Severity == NotificationSeverity.Critical
            && m.Title.Contains("新規建てを停止")
            // 手仕舞いまで止まったと読ませない（ADR-0022 決定5）。
            && m.Content.Contains("手仕舞い・損切りは止めていません"));

        await host.StopAsync();
    }

    // 🔴 **取引そのものの通知**（決定3）。状態の通知とは別に飛ぶ。
    [Fact]
    public async Task 鮮度切れでの決済は_観測日つきで通知する()
    {
        var (host, sender) = await BuildAsync();
        using var _ = host;

        var now = DateTimeOffset.UtcNow;
        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new PositionClosedWithStaleFxRate("7203", Market.Japan, "JPY", 300, 0.0067m, now.AddDays(-31), 31, now));
        session.Executed.MessagesOf<PositionClosedWithStaleFxRate>().Should().NotBeEmpty();

        sender.Sent.Should().ContainSingle(m =>
            m.Content.Contains("7203") && m.Content.Contains("観測日") && m.Content.Contains("乖離し得ます"));

        await host.StopAsync();
    }

    // FR-05, FR-09, FR-10, #331, IADR-0210/0211: 損切りの逆指値一本化で足した 3 イベントの通知。
    // 通知が飛ばないと、利用者は「発注されなかった」「建玉が勝手に消えた」ことに気付けない。

    [Fact]
    public async Task 見送りイベントは再試行されないことを伝える通知を送信する()
    {
        // 🔴 キューイングしない裁定（IADR-0211）の帰結を利用者へ伝えるのは通知だけである。
        // 「あとで自動的に発注される」と誤解されると、利用者は次の取引判断を待たずに放置してしまう。
        var (host, sender) = await BuildAsync();
        using var _ = host;

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderDispatchForgone(
            Guid.NewGuid(), Intent(), OrderDispatchForgoneReason.BrokerUnavailable, DateTimeOffset.UtcNow));
        session.Executed.MessagesOf<OrderDispatchForgone>().Should().NotBeEmpty();

        sender.Sent.Should().ContainSingle(m =>
            m.Severity == NotificationSeverity.Warning
            && m.Content.Contains("再試行されません"));

        await host.StopAsync();
    }

    [Fact]
    public async Task 保護逆指値の発注は統制が働いた記録としてInfoで通知する()
    {
        // 平常時に流れる通知であり Critical にすると実際に止まる事象が埋もれる（重大度の切り分け）。
        var (host, sender) = await BuildAsync();
        using var _ = host;

        var closeIntent = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            BrokerProvider.InternalPaper, 10, 950m, PositionEffect.Close);
        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new ProtectiveStopPlaced(
            Guid.NewGuid(), Guid.NewGuid(), "stop-1", closeIntent, 950m, 1, DateTimeOffset.UtcNow));
        session.Executed.MessagesOf<ProtectiveStopPlaced>().Should().NotBeEmpty();

        sender.Sent.Should().ContainSingle(m =>
            m.Severity == NotificationSeverity.Info && m.Content.Contains("stop-1"));

        await host.StopAsync();
    }

    [Fact]
    public async Task 保護喪失は建玉解消の内容つきでCriticalに通知する()
    {
        // 利用者の承認なしに建玉が消える事象であり、対処内容まで読めないと事後に何が起きたか分からない。
        var (host, sender) = await BuildAsync();
        using var _ = host;

        var closeIntent = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            BrokerProvider.InternalPaper, 10, 1_000m, PositionEffect.Close);
        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new ProtectiveStopCoverageLost(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, ProtectiveStopLossCause.LapsedInFlight,
            ProtectiveStopRemediation.PositionClosed, 10, Guid.NewGuid(), closeIntent, DateTimeOffset.UtcNow));
        session.Executed.MessagesOf<ProtectiveStopCoverageLost>().Should().NotBeEmpty();

        sender.Sent.Should().ContainSingle(m =>
            m.Severity == NotificationSeverity.Critical
            && m.Content.Contains("成行で手仕舞い"));

        await host.StopAsync();
    }

    // FR-09, FR-19, UC-06, #341, ADR-0025, ADR-0028 決定3, IADR-0241:
    // GFV 違反の計上は Critical で通知される。**発注前ガードのすり抜けが現に起きたこと**を知らせる唯一の経路であり、
    // 停止の解除窓口が Discord だけである以上、通知が無ければ利用者は解除が要ることに気付けない。
    [Fact]
    public async Task GFV違反の計上はガードのすり抜けとして通知する()
    {
        var (host, sender) = await BuildAsync();
        using var _ = host;

        var now = DateTimeOffset.UtcNow;
        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new GoodFaithViolationRecorded(
                Guid.NewGuid(), Guid.NewGuid(), "ORD-9", "AAPL", Market.UnitedStates, 12_345.67m, null,
                new DateOnly(2026, 8, 27), now, now));
        session.Executed.MessagesOf<GoodFaithViolationRecorded>().Should().NotBeEmpty();

        sender.Sent.Should().ContainSingle(m =>
            m.Severity == NotificationSeverity.Critical
            && m.Content.Contains("ORD-9")
            && m.Content.Contains("すり抜けた買付")
            && m.Content.Contains("/gfv clear"));

        await host.StopAsync();
    }
}
