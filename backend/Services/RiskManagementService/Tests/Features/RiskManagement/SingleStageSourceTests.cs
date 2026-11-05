using Microsoft.Extensions.Logging.Abstractions;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// 🔴 FR-20, ADR-0008, #1533, IADR-0616: 運用段階の正は段階ゲートの台帳 1 つ。段階の変更はゲートの遷移だけで行い、
// 遷移の受理で審査が読む設定の段階をゲートの定義へそろえる。段 1 は設定で審査して食い違いを記録し、段 2 は台帳の定義で審査する。
public class SingleStageSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);
    private static readonly StageGatePolicy Policy = TradingDefaults.CreateStagePolicy();

    private static RiskManagementSettings SettingsAt(TradingStage stage) =>
        TradingDefaults.CreateSettings() with { Stage = Policy.SettingsFor(stage) };

    [Fact]
    public void 段1は設定の段階をそのまま返す()
    {
        var inner = new InMemoryRiskSettingsStore(SettingsAt(TradingStage.Stage2MinimalLive));
        var store = new StageGateBoundRiskSettingsStore(
            inner, new InMemoryStageGateStore(TradingStage.Stage0Verification), Policy, StageSourceMode.Report,
            NullLogger<StageGateBoundRiskSettingsStore>.Instance);

        store.GetCurrent().Stage.Should().Be(Policy.SettingsFor(TradingStage.Stage2MinimalLive), "段 1 は審査の挙動を変えない");
    }

    [Fact]
    public void 段2は台帳の現段階の定義で返す()
    {
        // 設定だけが Stage 2 に書き換わっていても、台帳が Stage 0 なら審査は Stage 0 の定義（内蔵 paper・資金上限）で判定する。
        var inner = new InMemoryRiskSettingsStore(SettingsAt(TradingStage.Stage2MinimalLive));
        var store = new StageGateBoundRiskSettingsStore(
            inner, new InMemoryStageGateStore(TradingStage.Stage0Verification), Policy, StageSourceMode.Enforce,
            NullLogger<StageGateBoundRiskSettingsStore>.Instance);

        store.GetCurrent().Stage.Should().Be(Policy.SettingsFor(TradingStage.Stage0Verification));
        store.GetCurrent().Stage.Mode.Should().NotBe(BrokerProvider.MoomooReal);
    }

    [Fact]
    public void 段2で設定の口から書いた他の値は残る()
    {
        var inner = new InMemoryRiskSettingsStore(SettingsAt(TradingStage.Stage1Simulate) with { Stage1MinimumTradeCount = 123 });
        var store = new StageGateBoundRiskSettingsStore(
            inner, new InMemoryStageGateStore(TradingStage.Stage1Simulate), Policy, StageSourceMode.Enforce,
            NullLogger<StageGateBoundRiskSettingsStore>.Instance);

        store.GetCurrent().Stage1MinimumTradeCount.Should().Be(123);
    }

    [Fact]
    public void 遷移の受理で設定の段階がゲートの定義にそろう()
    {
        var clock = new FakeClock(Now, DateOnly.FromDateTime(Now.DateTime));
        var settingsStore = new InMemoryRiskSettingsStore(SettingsAt(TradingStage.Stage1Simulate));
        var log = new InMemorySettingsChangeLog();
        var settings = new RiskSettingsService(settingsStore, log, clock, FakeBrokerAccountObservations.NotObserved());
        var ledger = new InMemoryStageGateStore(TradingStage.Stage1Simulate);
        var svc = new StageGateService(
            ledger, new InMemoryStagePerformanceStore(), new InMemoryControlViolationObservationStore(),
            new InMemoryStage1FillObservationStore(), new InMemoryStage1TradingDayObservationStore(), Policy, settingsStore,
            new KillSwitchService(new InMemoryKillSwitchStore(), new InMemorySettingsChangeLog(), clock),
            new ShortSellReleaseSourceInventory([]), clock, settings);

        // 差し戻し（降格）は承認だけで受理される。
        var result = svc.RequestTransition(TradingStage.Stage0Verification, "owner");

        result.Accepted.Should().BeTrue();
        ledger.Load().CurrentStage.Should().Be(TradingStage.Stage0Verification);
        settingsStore.GetCurrent().Stage.Should().Be(Policy.SettingsFor(TradingStage.Stage0Verification));
        log.GetHistory().Should().Contain(e => e.ChangeType == SettingsChangeType.Stage && e.Actor == "owner");
    }

    [Fact]
    public void 拒否された遷移では設定の段階は変わらない()
    {
        var clock = new FakeClock(Now, DateOnly.FromDateTime(Now.DateTime));
        var settingsStore = new InMemoryRiskSettingsStore(SettingsAt(TradingStage.Stage0Verification));
        var settings = new RiskSettingsService(settingsStore, new InMemorySettingsChangeLog(), clock, FakeBrokerAccountObservations.NotObserved());
        var svc = new StageGateService(
            new InMemoryStageGateStore(TradingStage.Stage0Verification), new InMemoryStagePerformanceStore(),
            new InMemoryControlViolationObservationStore(), new InMemoryStage1FillObservationStore(),
            new InMemoryStage1TradingDayObservationStore(), Policy, settingsStore,
            new KillSwitchService(new InMemoryKillSwitchStore(), new InMemorySettingsChangeLog(), clock),
            new ShortSellReleaseSourceInventory([]), clock, settings);

        // 昇格は合格基準を満たさないと受理されない（実績が無い）。
        var result = svc.RequestTransition(TradingStage.Stage1Simulate, "owner");

        result.Accepted.Should().BeFalse();
        settingsStore.GetCurrent().Stage.Should().Be(Policy.SettingsFor(TradingStage.Stage0Verification));
    }

    [Theory]
    [InlineData(null, StageSourceMode.Report)]
    [InlineData("Report", StageSourceMode.Report)]
    [InlineData("Enforce", StageSourceMode.Enforce)]
    public void 段の構成を読む(string? value, StageSourceMode expected) =>
        StageSourceModeConfig.From(value).Should().Be(expected);

    [Fact]
    public void 知らない段は起動で止める()
    {
        var read = () => StageSourceModeConfig.From("Strict");
        read.Should().Throw<InvalidOperationException>();
    }

    // 🔴 段階を書く口は段階ゲートの遷移だけ。本番のソースで UpdateStage を呼ぶのは SyncStageFromGate だけであり、設定の口は呼ばない。
    [Fact]
    public void 本番のソースで段階を書くのはゲートの遷移だけ()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RiskManagementService.csproj")))
            dir = dir.Parent;
        dir.Should().NotBeNull();

        var callers = Directory.EnumerateFiles(dir!.FullName, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadAllLines(f).Select(l => (File: Path.GetFileName(f), Line: l)))
            .Where(x => x.Line.Contains(".UpdateStage(", StringComparison.Ordinal)
                || x.Line.Contains("SyncStageFromGate(", StringComparison.Ordinal) && !x.Line.Contains("public void", StringComparison.Ordinal))
            .ToList();

        callers.Select(c => c.File).Distinct().Should().BeEquivalentTo(["StageGateService.cs"]);
    }
}
