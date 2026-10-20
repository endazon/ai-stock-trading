using RiskManagementService.Domain;
using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Infrastructure.ExternalServices;
using RiskManagementService.Common.Abstractions;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Metrics;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Microsoft.Extensions.Logging;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, UC-01, UC-02: TradeDecisionMade 購読 → 承認/拒否発行を Wolverine のテストハーネス
// （Wolverine.Tracking）で検証する。
public class TradeDecisionMadeConsumerTests
{
    private const string ServiceName = "ai-stock-trading.risk-management-service";

    private static OrderIntent Entry() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 1_000m);

    // ADR-0013, IADR-0129, #354: MassTransit のテストハーネスから Wolverine.Tracking へ移行した。
    // 明示登録（AddConsumer<T>）は「規約発見を止めて対象型だけを含める」形へ写す
    // （テストの対象範囲を旧テストと同一に保つ）。実ブローカへは接続しない。
    private static Task<IHost> BuildHostAsync(
        IKillSwitchStore killSwitch, InMemoryPortfolioLedgerStore? ledger = null,
        InMemoryEntryReservationStore? reservations = null) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // #1396, IADR-0561: 実時刻が閉場前の締め切りに入っていても締め切りの外の時刻で審査する（結果を実行時刻に依らせない）。
                opts.Services.AddSingleton<IClock, OutsidePreCloseCutoffClock>();
                opts.Services.AddSingleton<IBusinessCalendar, WeekendBusinessCalendar>();
                opts.Services.AddSingleton<IRiskSettingsStore, InMemoryRiskSettingsStore>();
                opts.Services.AddSingleton(killSwitch);
                opts.Services.AddSingleton<IPauseStore, InMemoryPauseStore>();
                opts.Services.AddSingleton<ILockoutStore, InMemoryLockoutStore>();
                opts.Services.AddSingleton<ISettingsChangeLog, InMemorySettingsChangeLog>();
                opts.Services.AddSingleton<IPortfolioStateProvider>(new HealthyPortfolioProvider());
                // FR-20, #387, IADR-0148: 審査結果は段階ゲートの観測ログへ記録される（ハンドラの依存）。
                opts.Services.AddSingleton<IControlViolationObservationStore,
                    InMemoryControlViolationObservationStore>();
                // FR-19, #375, ADR-0021 決定3, IADR-0153: 口座種別の観測ストア（スナップショット組み立ての依存）。
                // 本テストの注文意図は内蔵 paper であり口座種別を要求しないため、観測は投入しない
                // （＝口座種別を確認できていない状態のまま検証する）。
                // #337, #564, IADR-0249, IADR-0267: 縮退状態は PortfolioSnapshotBuilder の必須依存。
                // 本テストは縮退を関心に持たないため「現況を観測できていて健全」を明示する
                // （実物のストアは観測が無いあいだ新規建てを止める＝不明は止める側）。
                opts.Services.AddSingleton<IInformationDegradationStore>(FakeInformationDegradation.Affirmed());
                opts.Services.AddSingleton<IBrokerAccountObservationStore>(
                    new InMemoryBrokerAccountObservationStore(TimeProvider.System));
                // FR-10, #869, ADR-0041 決定2, IADR-0354: 統制上限の基準資金はブローカーの口座照会由来。
                // 本テストの関心は承認・拒否の中継であり、基準資金は照会できている状態を与える
                // （与えないと CapitalBaselineUnavailable で全件が拒否される＝fail-closed）。
                opts.Services.AddSingleton<ICapitalBaselineStore>(FakeCapitalBaseline.Of(100_000m));
                opts.Services.AddSingleton<PortfolioSnapshotBuilder>();
                // NFR-07, #287, IADR-0255: 業務メトリクスはハンドラの**必須依存**である。
                // 本番では AddAiStockTradingObservability が登録する（BusinessMetricsWiringTests が固定）。
                opts.Services.AddSingleton<BusinessMetrics>();
                opts.Services.AddSingleton(sp => new OrderScreeningService(
                    sp.GetRequiredService<IRiskSettingsStore>(),
                    sp.GetRequiredService<PortfolioSnapshotBuilder>(),
                    sp.GetRequiredService<ILockoutStore>(),
                    sp.GetRequiredService<IClock>(),
                    sp.GetRequiredService<IBusinessCalendar>(),
                    // #428: 推定台帳は必須依存。本テストは強制買戻しを関心に持たないため空の台帳を渡す。
                    new InMemoryBuyInInferenceStore(),
                    // #935, IADR-0394: 台帳も必須依存（損切りした銘柄の同日・同方向の新規建ての入力）。空の台帳＝当日の損切りなし。
                    // #832, IADR-0407: 承認済みの判断の再配送を見分ける入力でもある（既定は空＝承認済みの判断なし）。
                    ledger ?? new InMemoryPortfolioLedgerStore(),
                    // #967, IADR-0425: 空売り文脈の供給も必須依存。本テストは空売りを関心に持たない（借株可否は常に分からない）。
                    TestShortSellContexts.Unavailable(sp.GetRequiredService<IClock>()),
                    // #1319, IADR-0529: 新規建ての審査の直列化と予約も必須依存。
                    reservations ?? new InMemoryEntryReservationStore(), TradingDefaults.CreatePreCloseEntryCutoff(), TradingDefaults.CreateThemeConcentration(),
                    null));

                // 本番と同じ配線（キュー名・fan-out・再試行・DLQ）を用い、送信先だけ stub へ倒す。
                // ルーティングを入れないと発行先が 1 つも無く、送信そのものが起きない。
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<TradeDecisionMadeHandler>();
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    [Fact]
    public async Task 承認された注文は_OrderApproved_を発行する()
    {
        using var host = await BuildHostAsync(new InMemoryKillSwitchStore());

        var session1 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new TradeDecisionMade(Guid.NewGuid(), Entry(), "判断", DateTimeOffset.UtcNow));

        session1.Executed.MessagesOf<TradeDecisionMade>().Should().NotBeEmpty();
        session1.Sent.MessagesOf<OrderApproved>().Should().NotBeEmpty();
        session1.Sent.MessagesOf<OrderRejected>().Should().BeEmpty();

        await host.StopAsync();
    }

    [Fact]
    public async Task kill_switch_起動中は_OrderRejected_を発行する()
    {
        var killSwitch = new InMemoryKillSwitchStore();
        killSwitch.SetState(new KillSwitchState(true, "user", "停止", DateTimeOffset.UtcNow));
        using var host = await BuildHostAsync(killSwitch);

        var session1 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new TradeDecisionMade(Guid.NewGuid(), Entry(), "判断", DateTimeOffset.UtcNow));

        session1.Sent.MessagesOf<OrderRejected>().Should().NotBeEmpty();
        var rejected = session1.Sent.MessagesOf<OrderRejected>().First();
        rejected.Reasons.Should().Contain(RejectionReason.KillSwitchActive);

        await host.StopAsync();
    }

    // FR-20, FR-11, #387, IADR-0148: **審査結果がメッセージ経路で観測ログへ記録される**（配線の確認）。
    // 記録が起きなければ集計は永久に未供給のままであり、Stage 1 → 2 は昇格できない（fail-safe だが不発）。
    // 発注先は算入対象（moomoo SIMULATE）とし、拒否理由はクラス B（緊急停止）＝件数は増えないが供給は作る。
    [Fact]
    public async Task 審査結果は観測ログへ記録される()
    {
        var killSwitch = new InMemoryKillSwitchStore();
        killSwitch.SetState(new KillSwitchState(true, "user", "停止", DateTimeOffset.UtcNow));
        using var host = await BuildHostAsync(killSwitch);
        var violations = host.Services.GetRequiredService<IControlViolationObservationStore>();
        violations.GetTally().Should().BeNull("観測が入るまでは未供給である");

        var intent = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 10, 1_000m);
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new TradeDecisionMade(Guid.NewGuid(), intent, "判断", DateTimeOffset.UtcNow));

        var tally = violations.GetTally();
        tally.Should().NotBeNull("審査が動けば集計は供給される");
        tally!.Count.Should().Be(0, "緊急停止による拒否はクラス B であり統制違反に計上しない");

        await host.StopAsync();
    }

    // NFR-07, FR-10, FR-19, #287, IADR-0255: **統制が効いていることが業務メトリクスで見える**（肯定形）。
    // 審査 1 件（outcome=rejected）と拒否理由の内訳（reason=KillSwitchActive）が実際に刻まれる。
    // 「拒否だけを数えない」設計であるため、承認側も同じ計器 ast.risk.screenings で数える。
    [Fact]
    public async Task 拒否の審査で統制メトリクスが実際に刻まれる()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        var killSwitch = new InMemoryKillSwitchStore();
        killSwitch.SetState(new KillSwitchState(true, "user", "停止", DateTimeOffset.UtcNow));
        using var host = await BuildHostAsync(killSwitch);

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new TradeDecisionMade(Guid.NewGuid(), Entry(), "判断", DateTimeOffset.UtcNow));

        capture.TagValuesOf(BusinessMetricNames.RiskScreenings, BusinessMetricNames.TagOutcome)
            .Should().Contain(BusinessMetrics.OutcomeRejected);
        capture.TagValuesOf(BusinessMetricNames.RiskRejections, BusinessMetricNames.TagReason)
            .Should().Contain(nameof(RejectionReason.KillSwitchActive));

        await host.StopAsync();
    }

    // NFR-07, FR-10, #287, IADR-0255: **承認でも審査回数を数える**（対の肯定形）。
    // 承認を数えないと「違反 0 件」と「そもそも審査が動いていない」を区別できない（#387 と同じ fail-open）。
    [Fact]
    public async Task 承認の審査でも審査回数が刻まれる()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        using var host = await BuildHostAsync(new InMemoryKillSwitchStore());

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new TradeDecisionMade(Guid.NewGuid(), Entry(), "判断", DateTimeOffset.UtcNow));

        capture.TagValuesOf(BusinessMetricNames.RiskScreenings, BusinessMetricNames.TagOutcome)
            .Should().Contain(BusinessMetrics.OutcomeApproved);

        await host.StopAsync();
    }

    // 🔴 T-10-874, T-10-875, FR-10, FR-20, #832, IADR-0407: 承認済み（自分の OrderApproved を台帳へ射影済み）の
    // 新規建ての判断が再配送されたら、本番と同じ配線のハンドラは**何も発行しない**（承認も拒否も）・例外にしない・
    // 観測ログを書かない。観測は最初の審査が発行より先に記録済みであり、再配送で書くと観測の件数が審査の件数を超える。
    // kill switch を起動した状態で流す——再審査していれば KillSwitchActive で OrderRejected が出る形であり、
    // 「何も発行しない」が再審査の抑止によることを示す（抑止が外れると拒否が出て赤になる）。
    [Fact]
    public async Task 承認済みの新規建ての判断の再配送では何も発行せず観測も記録しない()
    {
        var killSwitch = new InMemoryKillSwitchStore();
        killSwitch.SetState(new KillSwitchState(true, "user", "停止", DateTimeOffset.UtcNow));
        var ledger = new InMemoryPortfolioLedgerStore();
        var decisionId = Guid.NewGuid();
        ledger.AppendApproval(decisionId, Entry(), DateTimeOffset.UtcNow, source: ApprovalSource.OrderApproved);
        using var host = await BuildHostAsync(killSwitch, ledger);
        var violations = host.Services.GetRequiredService<IControlViolationObservationStore>();

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new TradeDecisionMade(decisionId, Entry(), "再配送", DateTimeOffset.UtcNow));

        session.Executed.MessagesOf<TradeDecisionMade>().Should().NotBeEmpty();
        session.Sent.MessagesOf<OrderApproved>().Should().BeEmpty("承認を発行し直さない");
        session.Sent.MessagesOf<OrderRejected>().Should().BeEmpty("承認済みの判断を拒否へ反転させない");
        violations.GetTally().Should().BeNull("再配送は新しい審査ではないので観測を書かない");

        await host.StopAsync();
    }

    // 予約（承認行は無い＝最初の承認の発行が届いていない）を置く。承認時刻と損切りの実行機構は、現在の設定と見分けられる値にする。
    private static (InMemoryEntryReservationStore Store, Guid DecisionId, OrderIntent Intent) Reserved(DateTimeOffset approvedAt)
    {
        var reservations = new InMemoryEntryReservationStore();
        var decisionId = Guid.NewGuid();
        // 発注先は moomoo SIMULATE（口座種別の観測が無いので、再審査すれば BrokerAccountTypeUnverified で拒否される形）。
        var intent = Entry() with { Mode = BrokerProvider.MoomooSimulate };
        reservations.Reserve(new EntryReservation(
            decisionId, intent.Symbol, intent.Market, intent.Side, intent.Quantity, intent.Price, intent.FxRateToBase,
            intent.Quantity, approvedAt, StopLossExecutionMethod.SoftwareStop));
        return (reservations, decisionId, intent);
    }

    // 🔴 T-10-2574, FR-10, #1319, IADR-0529 決定4: 審査は承認して予約を書いたが、OrderApproved の発行が失敗した（承認行が無い）
    // まま判断が再配送された。**新規建てを止める状態が無く、承認時刻の取引日が今日なら**、本番と同じ配線のハンドラは
    // 再審査せず、予約から組み直した最初と同じ承認を発行する（発注が永久に出ない状態にしない）。観測は記録する（DecisionId で冪等）。
    // 再審査していれば口座種別の未確認で拒否される発注先にしてあり、承認が予約からの組み直しであることを示す。
    [Fact]
    public async Task 予約済みで承認行が無い判断の再配送では止める状態が無ければ同じ承認を発行し直す()
    {
        var approvedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var (reservations, decisionId, intent) = Reserved(approvedAt);
        using var host = await BuildHostAsync(new InMemoryKillSwitchStore(), reservations: reservations);
        var violations = host.Services.GetRequiredService<IControlViolationObservationStore>();

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new TradeDecisionMade(decisionId, intent, "再配送", DateTimeOffset.UtcNow));

        var republished = session.Sent.MessagesOf<OrderApproved>().Should().ContainSingle("最初の承認の発行が届いていないので発行し直す").Subject;
        republished.DecisionId.Should().Be(decisionId);
        republished.ApprovedAt.Should().Be(approvedAt, "承認時刻は予約（最初の承認）のまま");
        republished.StopLossMethod.Should().Be(StopLossExecutionMethod.SoftwareStop, "損切りの実行機構は承認時点の値（現在の設定で埋めない）");
        republished.FromTradeDecision.Should().BeTrue();
        session.Sent.MessagesOf<OrderRejected>().Should().BeEmpty("再審査しない");
        violations.GetTally().Should().NotBeNull("観測は記録する（最初の記録の前に落ちた場合を埋める。DecisionId で冪等）");

        await host.StopAsync();
    }

    // 🔴 T-10-2585, FR-10, ADR-0003, #1319 の監査, IADR-0529 決定6: 発行の失敗の後に kill switch を起動し、そこへ再配送が来た。
    // 本番と同じ配線のハンドラは**承認を発行せず**、KillSwitchActive の拒否を発行する（緊急停止の後に新規建てを出さない）。
    [Fact]
    public async Task 予約済みで承認行が無い判断の再配送はkill_switchの後なら拒否を発行し承認を発行しない()
    {
        var killSwitch = new InMemoryKillSwitchStore();
        var (reservations, decisionId, intent) = Reserved(DateTimeOffset.UtcNow.AddSeconds(-1));
        killSwitch.SetState(new KillSwitchState(true, "user", "停止", DateTimeOffset.UtcNow));
        using var host = await BuildHostAsync(killSwitch, reservations: reservations);

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new TradeDecisionMade(decisionId, intent, "再配送", DateTimeOffset.UtcNow));

        session.Sent.MessagesOf<OrderApproved>().Should().BeEmpty("緊急停止の後に新規建ての承認を発行しない");
        session.Sent.MessagesOf<OrderRejected>().Should().ContainSingle()
            .Which.Reasons.Should().Equal(RejectionReason.KillSwitchActive);
        reservations.Find(decisionId)!.IsVoided.Should().BeTrue("予約は無効になり、以後は数えない");

        await host.StopAsync();
    }

    // T-10-2586, FR-10, #1319 の監査, IADR-0529 決定6: 承認時刻の取引日が過ぎた予約の再配送では、承認も拒否も発行せず、例外にもしない。
    [Fact]
    public async Task 取引日が過ぎた予約の再配送では何も発行しない()
    {
        var (reservations, decisionId, intent) = Reserved(DateTimeOffset.UtcNow.AddDays(-3));
        using var host = await BuildHostAsync(new InMemoryKillSwitchStore(), reservations: reservations);

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new TradeDecisionMade(decisionId, intent, "再配送", DateTimeOffset.UtcNow));

        session.Sent.MessagesOf<OrderApproved>().Should().BeEmpty("前の取引日の判断を今日の新規建てにしない");
        session.Sent.MessagesOf<OrderRejected>().Should().BeEmpty("拒否に当たる状態は無い");
        reservations.Find(decisionId)!.IsVoided.Should().BeTrue();

        await host.StopAsync();
    }

    // 全統制を通過する健全な運用状態（資金 10 万・損益ゼロ）。
    private sealed class HealthyPortfolioProvider : IPortfolioStateProvider
    {
        public PortfolioState GetCurrent() => new() { LedgerEquity = 100_000m };
    }
}
