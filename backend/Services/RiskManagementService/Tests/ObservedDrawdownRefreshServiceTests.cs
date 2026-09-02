using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Infrastructure.ExternalServices;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Common.Abstractions;
using RiskManagementService.Domain;
using RiskManagementService.Hosted;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace RiskManagementService.Tests;

// FR-20, FR-10, UC-06, ADR-0008, IADR-0103, #164: 実DD 供給ドライバの結線を検証する。
// 供給（単調 latch）・休場ガード・無変化時の非保存・サンプリング失敗時の fail-safe を受け入れ基準へ写像する。
// ドライバは巡回ごとに DI スコープから scoped な依存を解決するため、テストも DI コンテナ上で構成する。
public class ObservedDrawdownRefreshServiceTests
{
    // 2026-07-17 金曜（営業日）/ 2026-07-18 土曜（休場）。
    private static readonly DateTimeOffset FridayUtc = new(2026, 7, 17, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SaturdayUtc = new(2026, 7, 18, 9, 0, 0, TimeSpan.Zero);

    private static (ObservedDrawdownRefreshService driver, RecordingStagePerformanceStore store, StubPortfolioStateProvider provider)
        Build(DateTimeOffset now, decimal drawdownRatio = 0m, bool throwOnSample = false)
    {
        var store = new RecordingStagePerformanceStore();
        var provider = new StubPortfolioStateProvider(drawdownRatio, throwOnSample);
        var services = new ServiceCollection()
            .AddSingleton<IStagePerformanceStore>(store)
            .AddSingleton<IPortfolioStateProvider>(provider)
            .BuildServiceProvider();

        var driver = new ObservedDrawdownRefreshService(
            services.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(now),
            new WeekendBusinessCalendar(),
            Options.Create(new ObservedDrawdownRefreshOptions()),
            NullLogger<ObservedDrawdownRefreshService>.Instance);

        return (driver, store, provider);
    }

    [Fact]
    public async Task 実DDを段階別実績へ供給する()
    {
        // 受け入れ基準 5（#164）: 実DD を IStagePerformanceStore へ供給し、撤退基準（ADR-0008）の入力を満たす。
        var (driver, store, _) = Build(FridayUtc, drawdownRatio: 0.20m);

        await driver.RunOnceAsync(CancellationToken.None);

        store.GetCurrent().ObservedMaxDrawdownRatio.Should().Be(0.20m);
        store.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task 回復しても実DDの最大値は下がらない()
    {
        // 受け入れ基準 1: 「観測した最大 DD」なので、資金が回復して現在 DD が下がっても latch は維持する。
        var (driver, store, provider) = Build(FridayUtc, drawdownRatio: 0.20m);

        await driver.RunOnceAsync(CancellationToken.None);
        provider.DrawdownRatio = 0.05m;
        await driver.RunOnceAsync(CancellationToken.None);

        store.GetCurrent().ObservedMaxDrawdownRatio.Should().Be(0.20m);
        store.SaveCount.Should().Be(1, "2 巡目は最大値が変わらないため保存しない");
    }

    [Fact]
    public async Task 実DD以外のフィールドは供給で温存する()
    {
        // 受け入れ基準 3（IADR-0089 の鏡像）: backtest 由来のフィールドを実DD 供給が上書きしない。
        var (driver, store, _) = Build(FridayUtc, drawdownRatio: 0.20m);
        store.Save(new StagePerformance { BacktestPassed = true, BacktestMaxDrawdownRatio = 0.10m });

        await driver.RunOnceAsync(CancellationToken.None);

        var performance = store.GetCurrent();
        performance.ObservedMaxDrawdownRatio.Should().Be(0.20m);
        performance.BacktestPassed.Should().BeTrue();
        performance.BacktestMaxDrawdownRatio.Should().Be(0.10m);
    }

    [Fact]
    public async Task 休場日はサンプリングしない()
    {
        // 受け入れ基準 6: 市場休場ガード（#21・IADR-0083 と同型）。非営業日は台帳・時価にも触れない。
        var (driver, store, provider) = Build(SaturdayUtc, drawdownRatio: 0.20m);

        await driver.RunOnceAsync(CancellationToken.None);

        provider.SampleCount.Should().Be(0);
        store.SaveCount.Should().Be(0);
        store.GetCurrent().ObservedMaxDrawdownRatio.Should().Be(0m);
    }

    [Fact]
    public async Task 更新が無ければ保存しない()
    {
        // 受け入れ基準 7: 時価評価が既定無効（DD=0・IADR-0066）なら巡回しても書き込みが起きない＝既定は不活性。
        var (driver, store, provider) = Build(FridayUtc, drawdownRatio: 0m);

        await driver.RunOnceAsync(CancellationToken.None);

        provider.SampleCount.Should().Be(1);
        store.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task サンプリング失敗時は段階別実績を書き換えない_failsafe()
    {
        // 受け入れ基準 8（#164 fail-safe）: 供給不達・取得失敗時は既定／既存値を維持する（安全側）。
        // 例外は ExecuteAsync のループが捕捉して次周期へ縮退する（IADR-0083 と同型）。
        var (driver, store, _) = Build(FridayUtc, throwOnSample: true);
        store.Save(new StagePerformance { ObservedMaxDrawdownRatio = 0.12m });
        var savesBefore = store.SaveCount;

        var act = () => driver.RunOnceAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        store.GetCurrent().ObservedMaxDrawdownRatio.Should().Be(0.12m);
        store.SaveCount.Should().Be(savesBefore, "取得失敗時は書き込まない（部分更新を残さない）");
    }

    [Fact]
    public async Task 差し戻しリセット後は現在DDから観測しなおす()
    {
        // IADR-0103 決定3/7: 差し戻し（Demotion）で観測窓を 0 に戻したあと、ドライバは過去のピークではなく
        // その時点の現在 DD から latch をやり直す＝観測窓が本当に区切られている。
        var (driver, store, provider) = Build(FridayUtc, drawdownRatio: 0.20m);

        await driver.RunOnceAsync(CancellationToken.None);
        store.GetCurrent().ObservedMaxDrawdownRatio.Should().Be(0.20m);

        // 差し戻しによる観測窓リセット（StageGateService.RequestTransition と同じ射影を使う）。
        store.Save(StagePerformanceProjection.WithoutObservedDrawdown(store.GetCurrent()));
        provider.DrawdownRatio = 0.05m;

        await driver.RunOnceAsync(CancellationToken.None);

        store.GetCurrent().ObservedMaxDrawdownRatio.Should().Be(0.05m, "過去のピークを復活させず現在 DD から観測しなおす");
    }

    [Fact]
    public async Task 実DD供給で撤退基準に到達し自動停止する_通し()
    {
        // 受け入れ基準 10（#164）: verdict（バックテスト最大DD）＋実DD の供給が揃うと ADR-0008 の撤退基準
        // （実DD ≥ バックテスト最大DD × 1.5）に到達し、自動停止（kill switch）まで通る。
        // 供給前は fail-safe（実DD=0）で非発火であることも同じ経路で確認する。
        var (driver, store, provider) = Build(FridayUtc, drawdownRatio: 0m);
        // verdict 供給済み（BacktestEvaluatedProjectionConsumer 相当）を前提に置く。
        store.Save(new StagePerformance { BacktestPassed = true, BacktestMaxDrawdownRatio = 0.10m });

        var clock = new FixedClock(FridayUtc);
        var killSwitch = new KillSwitchService(
            new InMemoryKillSwitchStore(), new InMemorySettingsChangeLog(), clock);
        var stageGate = new StageGateService(
            new InMemoryStageGateStore(TradingStage.Stage2MinimalLive),
            store,
            new InMemoryControlViolationObservationStore(),
            new InMemoryStage1FillObservationStore(),
            new InMemoryStage1TradingDayObservationStore(),
            TradingDefaults.CreateStagePolicy(),
            // #423, IADR-0164 決定4: 最小取引件数は設定値であり、段階ゲートは設定ストアから実効値を読む。
            new InMemoryRiskSettingsStore(),
            killSwitch,
            new ShortSellReleaseSourceInventory([]),
            clock);

        // 実DD 未供給（0）では撤退は発火しない＝fail-safe。
        await driver.RunOnceAsync(CancellationToken.None);
        stageGate.EvaluateWithdrawal().Assessment.Triggered.Should().BeFalse();
        killSwitch.GetState().Engaged.Should().BeFalse();

        // 実DD が 0.10 × 1.5 = 0.15 を超えて観測される。
        provider.DrawdownRatio = 0.20m;
        await driver.RunOnceAsync(CancellationToken.None);

        var outcome = stageGate.EvaluateWithdrawal();
        outcome.Assessment.Triggered.Should().BeTrue();
        outcome.Assessment.Reason.Should().Be(WithdrawalReason.DrawdownBreachedMultiple);
        outcome.Assessment.ProposedStage.Should().Be(TradingStage.Stage0Verification);
        outcome.NewlyEngaged.Should().BeTrue();
        killSwitch.GetState().Engaged.Should().BeTrue();
    }

    // 保存回数を数えるインメモリストア（無用な書き込みが起きていないことの検証用）。
    private sealed class RecordingStagePerformanceStore : IStagePerformanceStore
    {
        private StagePerformance _performance = new();

        public int SaveCount { get; private set; }

        public StagePerformance GetCurrent() => _performance;

        public void Save(StagePerformance performance)
        {
            _performance = performance;
            SaveCount++;
        }
    }

    // DD だけを制御するポートフォリオ状態のスタブ。サンプリング回数も数える（休場ガードの検証用）。
    private sealed class StubPortfolioStateProvider(decimal drawdownRatio, bool throwOnSample) : IPortfolioStateProvider
    {
        public decimal DrawdownRatio { get; set; } = drawdownRatio;

        public int SampleCount { get; private set; }

        public PortfolioState GetCurrent()
        {
            SampleCount++;
            if (throwOnSample)
            {
                throw new InvalidOperationException("時価取得に失敗しました（テスト）");
            }

            return new PortfolioState { Capital = TradingDefaults.InitialCapital, DrawdownRatio = DrawdownRatio };
        }
    }

    // 休場ガードの当日判定を制御するための固定時計。
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;

        public DateOnly Today => DateOnly.FromDateTime(now.UtcDateTime);
    }
}
