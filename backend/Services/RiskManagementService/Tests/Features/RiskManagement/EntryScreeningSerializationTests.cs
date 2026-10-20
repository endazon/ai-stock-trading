using RiskManagementService.Common.Abstractions;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.ExternalServices;
using RiskManagementService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// 🔴 T-10-2568〜T-10-2573, T-10-2575, T-10-2576, T-10-2578, FR-10, FR-20, UC-01, UC-02, ADR-0003, ADR-0009, ADR-0016 決定9,
// #1319, IADR-0529: 新規建ての審査の直列化と予約。
//
// 事象（#1319・PoC 2026-10-12）: 定時サイクルの判断 N 件は同時に届き、承認行は OrderApproved が RabbitMQ を往復した後にしか
// 書かれない。審査が台帳だけを読むと N 件すべてが同じ古い snapshot で保有建玉数（既定 3）・日次発注累計・段階資金を通る。
//
// 組み立ては本番と同じ部品（実台帳・実注文源・実射影・実スクリーニング）で、**台帳には何も射影しない**
// （OrderApproved の往復が終わる前＝窓の中を再現する）。予約の供給を注文源へ渡すのは本番の EfWorkingEntryOrderSource と同じ。
public class EntryScreeningSerializationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 12);
    private const decimal Equity = 100_000m;

    private sealed record Stores(
        InMemoryPortfolioLedgerStore Ledger,
        InMemoryOrderActivityStore Activity,
        InMemoryEntryReservationStore Reservations);

    private static Stores NewStores() =>
        new(new InMemoryPortfolioLedgerStore(), new InMemoryOrderActivityStore(), new InMemoryEntryReservationStore());

    // 読み取りの後に待ちを入れて、並行な審査どうしの窓を広げる（直列化が無ければ全件が同じ snapshot を読む）。
    private sealed class SlowStateProvider(IPortfolioStateProvider inner, TimeSpan delay) : IPortfolioStateProvider
    {
        public PortfolioState GetCurrent()
        {
            var state = inner.GetCurrent();
            Thread.Sleep(delay);
            return state;
        }
    }

    // 新規建てを止める状態（kill switch・一時停止・ロックアウト）と時計は、再配送の検査（IADR-0529 決定6）のために差し替えられる。
    private sealed record Controls(
        InMemoryKillSwitchStore KillSwitch, InMemoryPauseStore Pause, InMemoryLockoutStore Lockout, FakeClock Clock)
    {
        public static Controls New() =>
            new(new InMemoryKillSwitchStore(), new InMemoryPauseStore(), new InMemoryLockoutStore(), new FakeClock(Now, Today));
    }

    private static OrderScreeningService Screening(
        Stores stores, RiskManagementSettings settings, TimeSpan? readDelay = null, IRiskSettingsStore? settingsStore = null,
        Controls? controls = null)
    {
        controls ??= Controls.New();
        var clock = controls.Clock;
        IPortfolioStateProvider provider = new LedgerPortfolioStateProvider(
            stores.Ledger, new InMemoryWorkingEntryOrderSource(stores.Ledger, stores.Activity, stores.Reservations), clock);
        if (readDelay is { } delay)
            provider = new SlowStateProvider(provider, delay);
        var snapshots = new PortfolioSnapshotBuilder(
            provider, controls.KillSwitch, controls.Pause,
            new InMemoryBrokerAccountObservationStore(TimeProvider.System),
            FakeInformationDegradation.Affirmed(), capitalBaseline: FakeCapitalBaseline.Of(Equity));
        return new OrderScreeningService(
            settingsStore ?? new InMemoryRiskSettingsStore(settings), snapshots, controls.Lockout, clock,
            new WeekendBusinessCalendar(), new InMemoryBuyInInferenceStore(), stores.Ledger,
            TestShortSellContexts.Unavailable(clock, stores.Ledger), stores.Reservations, TradingDefaults.CreatePreCloseEntryCutoff());
    }

    // 米国株・内蔵 paper（口座種別を要求しない）の新規買い。
    private static OrderIntent Entry(string symbol, int quantity = 10, decimal price = 10m) =>
        new(symbol, Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, quantity, price);

    private static OrderIntent Close(string symbol) =>
        new(symbol, Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.InternalPaper, 10, 10m,
            PositionEffect.Close);

    private static TradeDecisionMade Decision(OrderIntent intent) => new(Guid.NewGuid(), intent, "定時判断", Now);

    public enum Control
    {
        MaxPositions,
        DailyOrderAmount,
        StageCapital,
    }

    // 各統制を「2 件までは通り、3 件目以降で掛かる」形にする（他の統制は掛からない）。
    // - 保有建玉数: 上限 3（既定）を 2 へ。1 件 $100。
    // - 日次発注累計: equity $100,000 × 50% ＝ $50,000。1 件 200 株 × $100 ＝ $20,000（3 件目で $60,000）。
    // - 段階資金: 発注可能額 equity × 50% ＝ $50,000。1 件 $20,000。
    private static (RiskManagementSettings Settings, Func<string, OrderIntent> Intent, RejectionReason Reason) Arrange(Control control)
    {
        var defaults = TradingDefaults.CreateSettings();
        var roomy = defaults.Limits with { MaxOpenPositions = 10, MaxDailyOrderAmountRatio = 10m };
        return control switch
        {
            Control.MaxPositions => (
                defaults with { Limits = defaults.Limits with { MaxOpenPositions = 2 } },
                s => Entry(s), RejectionReason.MaxPositionsExceeded),
            Control.DailyOrderAmount => (
                defaults with { Limits = roomy with { MaxDailyOrderAmountRatio = 0.5m } },
                s => Entry(s, 200, 100m), RejectionReason.DailyOrderAmountExceeded),
            Control.StageCapital => (
                defaults with { Limits = roomy, Stage = defaults.Stage with { CapitalCapRatio = 0.5m } },
                s => Entry(s, 200, 100m), RejectionReason.StageCapitalCapExceeded),
            _ => throw new ArgumentOutOfRangeException(nameof(control)),
        };
    }

    private static readonly string[] Symbols = ["AAPL", "MSFT", "NVDA", "AMZN", "META", "GOOG"];

    // 🔴 T-10-2568（保有建玉数）・T-10-2569（日次発注累計）・T-10-2570（段階資金）: 台帳に何も反映されていない状態で、
    // 新規建ての判断を続けて 6 件審査する。上限までは承認、残りはその統制で拒否される（先に承認した分を予約として数える）。
    // 是正前は 6 件とも承認された（どれも同じ空の snapshot を読む）。
    [Theory]
    [InlineData(Control.MaxPositions)]
    [InlineData(Control.DailyOrderAmount)]
    [InlineData(Control.StageCapital)]
    public async Task 台帳に反映される前に続けて届いた新規建ては上限を超える分が拒否される(Control control)
    {
        var (settings, intent, reason) = Arrange(control);
        var stores = NewStores();
        var screening = Screening(stores, settings);

        var outcomes = new List<ScreeningOutcome>();
        foreach (var symbol in Symbols)
            outcomes.Add(await screening.ScreenAsync(Decision(intent(symbol))));

        outcomes.Count(o => o.IsApproved).Should().Be(2, "上限までの 2 件だけが承認される（是正前は 6 件）");
        outcomes.Take(2).Should().AllSatisfy(o => o.IsApproved.Should().BeTrue("先着の 2 件は通る"));
        outcomes.Skip(2).Should().AllSatisfy(o => o.Rejected!.Reasons.Should().Contain(reason));
        stores.Reservations.Snapshot().Should().HaveCount(2, "予約は承認した分だけ（拒否は予約しない）");
    }

    // 🔴 T-10-2571: **並行に**審査しても同じ（2 件以上を同時に ScreenAsync）。審査ごとに別のサービス（本番の scope ごとの生成）を作り、
    // 読み取りの後に待ちを入れて窓を広げる。直列化を外す変異では 6 件とも承認された（spec の実測）。
    [Theory]
    [InlineData(Control.MaxPositions)]
    [InlineData(Control.DailyOrderAmount)]
    [InlineData(Control.StageCapital)]
    public async Task 並行に届いた新規建ても上限を超える分が拒否される(Control control)
    {
        var (settings, intent, reason) = Arrange(control);
        var stores = NewStores();
        using var start = new ManualResetEventSlim(false);

        // 専用スレッドで同時に走らせる（スレッドプールの注入待ちで審査が自然に順番になり、直列化が無くても緑になる形を避ける）。
        var tasks = Symbols
            .Select(symbol => Task.Factory.StartNew(
                () =>
                {
                    var screening = Screening(stores, settings, readDelay: TimeSpan.FromMilliseconds(30));
                    start.Wait();
                    return screening.ScreenAsync(Decision(intent(symbol))).GetAwaiter().GetResult();
                },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToList();
        start.Set();
        var outcomes = await Task.WhenAll(tasks);

        outcomes.Count(o => o.IsApproved).Should().Be(2, "並行でも上限までの 2 件だけが承認される");
        outcomes.Where(o => !o.IsApproved).Should().AllSatisfy(o => o.Rejected!.Reasons.Should().Contain(reason));
        stores.Reservations.Snapshot().Should().HaveCount(2);
    }

    // 🔴 T-10-2572（IADR-0407 を変えない）: 承認行がある判断の再配送は、予約があっても再審査せず何も発行しない形を返す。
    // 上限 1 で自分の予約・承認行が枠を埋めている（再審査すれば MaxPositionsExceeded に反転する状態）。
    [Fact]
    public async Task 承認行がある判断の再配送は予約があっても再審査しない()
    {
        var defaults = TradingDefaults.CreateSettings();
        var stores = NewStores();
        var screening = Screening(stores, defaults with { Limits = defaults.Limits with { MaxOpenPositions = 1 } });
        var decision = Decision(Entry("AAPL"));
        var first = await screening.ScreenAsync(decision);
        first.IsApproved.Should().BeTrue();
        stores.Ledger.AppendApproval(decision.DecisionId, decision.Intent, first.Approved!.ApprovedAt);

        var replay = await screening.ScreenAsync(decision);

        replay.IsApprovedReplay.Should().BeTrue("承認行があれば発行済み（IADR-0407）");
        replay.Approved.Should().BeNull("承認を発行し直さない");
        replay.Rejected.Should().BeNull("拒否へ反転しない");
    }

    // 🔴 T-10-2573: 承認して予約を書いた後に OrderApproved の発行が失敗した（承認行が無い）まま判断が再配送されたら、
    // **再審査せず、予約から最初と同じ承認を組み直す**（発注が永久に出ない状態にしない）。
    // ［#1319 の監査］組み直すのは**新規建てを止める状態が無く、承認時刻の取引日が今日のとき**だけ（止める場合は T-10-2583・T-10-2584）。
    // 上限 1 で自分の予約が枠を埋めている（再審査すれば自分の予約で自分を拒否する）。設定（損切りの実行機構）も
    // 途中で変える——組み直しは**承認時点の値**を使い、現在の設定で埋めない（IADR-0342 決定3）。
    [Fact]
    public async Task 発行に失敗して再配送された新規建ては最初と同じ承認を発行し直す()
    {
        var defaults = TradingDefaults.CreateSettings() with { StopLossMethod = StopLossExecutionMethod.BrokerStopOrder };
        var settingsStore = new InMemoryRiskSettingsStore(defaults with { Limits = defaults.Limits with { MaxOpenPositions = 1 } });
        var stores = NewStores();
        var screening = Screening(stores, defaults, settingsStore: settingsStore);
        var decision = Decision(Entry("AAPL"));
        var first = await screening.ScreenAsync(decision);
        first.IsApproved.Should().BeTrue();
        // 発行が失敗した（承認行は書かれない）。その後に設定が変わる。
        settingsStore.Save(settingsStore.GetCurrent() with { StopLossMethod = StopLossExecutionMethod.SoftwareStop });

        var redelivered = await screening.ScreenAsync(decision);

        redelivered.IsApprovedRepublish.Should().BeTrue("予約があり承認行が無い＝最初の承認の発行が届いていない");
        redelivered.IsApproved.Should().BeTrue();
        redelivered.Rejected.Should().BeNull("自分の予約で自分を拒否しない");
        redelivered.Approved.Should().BeEquivalentTo(first.Approved, "最初と同じ承認（承認時刻・数量・損切りの実行機構）");
        stores.Reservations.Snapshot().Should().ContainSingle("再配送で予約を二重に書かない");
    }

    // 🔴 T-10-2575: 予約が承認行（OrderApproved の射影）へ引き継がれた後は、承認行だけで数える（二重計上しない）。
    [Fact]
    public async Task 予約が承認行へ引き継がれても二重に数えない()
    {
        var stores = NewStores();
        var screening = Screening(stores, TradingDefaults.CreateSettings());
        var decision = Decision(Entry("AAPL", 200, 100m));
        var approved = (await screening.ScreenAsync(decision)).Approved!;
        var clock = new FakeClock(Now, Today);
        var provider = new LedgerPortfolioStateProvider(
            stores.Ledger, new InMemoryWorkingEntryOrderSource(stores.Ledger, stores.Activity, stores.Reservations), clock);

        var reservedOnly = provider.GetCurrent();
        // 本番のハンドラチェーンと同じ 2 射影（台帳の承認行・注文アクティビティ）。
        stores.Ledger.AppendApproval(decision.DecisionId, decision.Intent, approved.ApprovedAt);
        stores.Activity.RecordPlacement(decision.DecisionId, "AAPL", Market.UnitedStates, TradeSide.Buy, 200, approved.ApprovedAt);
        var projected = provider.GetCurrent();

        reservedOnly.OpenPositionCount.Should().Be(1, "承認行が無い間は予約で数える");
        reservedOnly.DailyOrderedAmount.Should().Be(20_000m);
        projected.OpenPositionCount.Should().Be(1, "予約と承認行の両方で数えない");
        projected.DailyOrderedAmount.Should().Be(20_000m);
        projected.InvestedCapital.Should().Be(20_000m);
    }

    // 🔴 T-10-2576: 取消・失効・見送りの後は枠が返る（IADR-0346 決定2・決定5）。承認行が届く前（予約で数えている間）に終端が届いても返る。
    [Theory]
    [InlineData("取消", false)]
    [InlineData("失効", false)]
    [InlineData("見送り", false)]
    [InlineData("取消", true)]
    [InlineData("失効", true)]
    [InlineData("見送り", true)]
    public async Task 取消失効見送りの後は枠が返る(string kind, bool projectedFirst)
    {
        var defaults = TradingDefaults.CreateSettings();
        var stores = NewStores();
        var screening = Screening(stores, defaults with { Limits = defaults.Limits with { MaxOpenPositions = 1 } });
        var decision = Decision(Entry("AAPL"));
        var approvedAt = (await screening.ScreenAsync(decision)).Approved!.ApprovedAt;
        (await screening.ScreenAsync(Decision(Entry("MSFT")))).Rejected!.Reasons
            .Should().Contain(RejectionReason.MaxPositionsExceeded, "前提: 予約が枠を埋めている");

        // 注文アクティビティの行は承認の射影（同じハンドラチェーン）が作る。承認行（台帳）より先に書かれ得るので、
        // projectedFirst=false は「注文アクティビティだけ届き、承認行はまだ」＝予約で数えている間に終端が届く形である。
        // （見送りは行が無くても終端の行を作る・IADR-0346 決定5。取消・失効は行が無ければ記録されない既存の規則のまま）
        stores.Activity.RecordPlacement(decision.DecisionId, "AAPL", Market.UnitedStates, TradeSide.Buy, 10, approvedAt);
        if (projectedFirst)
            stores.Ledger.AppendApproval(decision.DecisionId, decision.Intent, approvedAt);

        switch (kind)
        {
            case "取消":
                stores.Activity.RecordExecution(decision.DecisionId, OrderStatus.Cancelled, 0, Now.AddSeconds(30));
                break;
            case "失効":
                stores.Activity.RecordExecution(decision.DecisionId, OrderStatus.Expired, 0, Now.AddSeconds(30));
                break;
            default:
                stores.Activity.RecordForgone(decision.DecisionId, "AAPL", Market.UnitedStates, TradeSide.Buy, 10, Now.AddSeconds(1));
                break;
        }

        var next = await screening.ScreenAsync(Decision(Entry("MSFT")));
        next.IsApproved.Should().BeTrue($"{kind}で終端になった注文は枠を返す");
    }

    // 🔴 T-10-2578（ADR-0009）: 手仕舞い（Close）は直列化の待ちに入らない（新規建ての審査が臨界区間を握っていても
    // 待たずに承認される）。予約も書かない。
    [Fact]
    public async Task 手仕舞いは新規建ての直列化で待たされず予約もされない()
    {
        var stores = NewStores();
        var screening = Screening(stores, TradingDefaults.CreateSettings());
        var release = new TaskCompletionSource();
        var held = stores.Reservations.RunSerializedAsync(async _ =>
        {
            await release.Task;
            return 0;
        }, CancellationToken.None);

        var close = screening.ScreenAsync(Decision(Close("AAPL")));
        var entry = screening.ScreenAsync(Decision(Entry("MSFT")));
        var finished = await Task.WhenAny(close, Task.Delay(TimeSpan.FromSeconds(10)));

        finished.Should().BeSameAs(close, "手仕舞いは新規建ての臨界区間を待たない");
        (await close).IsApproved.Should().BeTrue();
        entry.IsCompleted.Should().BeFalse("前提: 新規建ては臨界区間を待っている");
        release.SetResult();
        await held;
        (await entry).IsApproved.Should().BeTrue();
        stores.Reservations.Snapshot().Should().ContainSingle(r => r.Symbol == "MSFT", "予約は新規建てだけ");
    }

    public enum Blocker
    {
        KillSwitch,
        Pause,
        Lockout,
    }

    private static RejectionReason Engage(Controls controls, Blocker blocker)
    {
        switch (blocker)
        {
            case Blocker.KillSwitch:
                controls.KillSwitch.SetState(new KillSwitchState(true, "owner", "緊急停止", Now));
                return RejectionReason.KillSwitchActive;
            case Blocker.Pause:
                controls.Pause.SetState(new PauseState(true, "owner", "一時停止", Now));
                return RejectionReason.TradingPaused;
            default:
                controls.Lockout.Set(new LockoutState(Today.AddDays(1), "日次損失上限", Now));
                return RejectionReason.DailyLossLimitReached;
        }
    }

    // 🔴 T-10-2583（#1319 の監査・指摘 2）: 承認して予約を書いた後に発行が失敗し、**その後に新規建てを止める状態になった**
    // （kill switch・一時停止・ロックアウト）。再配送は承認を組み直さず、その理由で拒否する。予約は無効になり、
    // 以後は数えず、状態を解いた後の再配送でも再発行しない（止める側へ倒す）。
    // 是正前は緊急停止の後に新規建ての承認が発行された（発注執行は kill switch を見ない）。
    [Theory]
    [InlineData(Blocker.KillSwitch)]
    [InlineData(Blocker.Pause)]
    [InlineData(Blocker.Lockout)]
    public async Task 発行に失敗した後に新規建てを止める状態になったら再配送は拒否され予約は無効になる(Blocker blocker)
    {
        var stores = NewStores();
        var controls = Controls.New();
        var screening = Screening(stores, TradingDefaults.CreateSettings(), controls: controls);
        var decision = Decision(Entry("AAPL"));
        (await screening.ScreenAsync(decision)).IsApproved.Should().BeTrue("前提: 最初の審査は承認して予約を書く");
        var reason = Engage(controls, blocker);

        var redelivered = await screening.ScreenAsync(decision);

        redelivered.IsApproved.Should().BeFalse("止める状態の後に新規建ての承認を発行しない");
        redelivered.IsApprovedRepublish.Should().BeFalse();
        redelivered.Rejected!.Reasons.Should().Equal(reason);
        stores.Reservations.Find(decision.DecisionId)!.IsVoided.Should().BeTrue("予約は無効になる");
        new InMemoryWorkingEntryOrderSource(stores.Ledger, stores.Activity, stores.Reservations)
            .GetWorkingEntryOrders(Now.AddDays(-2)).Should().BeEmpty("無効の予約は数えない");

        // 状態を解いても、同じ判断は再発行しない（無効は戻さない）。
        controls.KillSwitch.SetState(KillSwitchState.Disengaged);
        controls.Pause.SetState(new PauseState(false));
        controls.Lockout.Clear();
        var again = await screening.ScreenAsync(decision);
        again.IsApproved.Should().BeFalse("無効にした予約は状態を解いた後も再発行しない");
        again.Rejected!.Reasons.Should().Equal(reason);
    }

    // 🔴 T-10-2584（#1319 の監査・指摘 2）: 承認時刻の取引日が過ぎた予約の再配送は、何も発行しない（前の取引日の判断を
    // 今日の新規建てにしない）。拒否に当たる状態は無いので拒否もしない。予約は無効になり、再度の再配送でも発行しない。
    [Fact]
    public async Task 承認時刻の取引日が過ぎた予約の再配送は承認を発行し直さない()
    {
        var stores = NewStores();
        var controls = Controls.New();
        var screening = Screening(stores, TradingDefaults.CreateSettings(), controls: controls);
        var decision = Decision(Entry("AAPL"));
        (await screening.ScreenAsync(decision)).IsApproved.Should().BeTrue();
        controls.Clock.UtcNow = Now.AddDays(1);
        controls.Clock.Today = Today.AddDays(1);

        var redelivered = await screening.ScreenAsync(decision);
        var again = await screening.ScreenAsync(decision);

        redelivered.IsReservationExpired.Should().BeTrue();
        redelivered.Approved.Should().BeNull("前の取引日の判断を今日の新規建てにしない");
        redelivered.Rejected.Should().BeNull("拒否に当たる状態は無い");
        stores.Reservations.Find(decision.DecisionId)!.IsVoided.Should().BeTrue();
        again.IsReservationExpired.Should().BeTrue("無効にした予約は再発行しない");
    }
}
