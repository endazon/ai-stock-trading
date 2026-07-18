using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.RiskManagement.Worker.Composable.StageGate;
using AiStockTrading.Shared.Contracts.Events;
using FluentAssertions;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiStockTrading.RiskManagement.Worker.Tests;

// FR-20, FR-11, FR-09, UC-06, ADR-0008, IADR-0083, #166: 撤退の定期評価ドライバの結線を検証する。
// 休場ガード・新規停止時のみ通知（冪等）・fail-safe 非発火・多重実行防止（逐次）を受け入れ基準へ写像する。
// WebApplicationFactory（InMemory DB・MassTransit ハーネス）上で、ドライバを直接構成して RunOnceAsync を叩く。
public class WithdrawalEvaluationServiceTests
{
    // 2026-07-17 金曜（営業日）/ 2026-07-18 土曜（休場）。
    private static readonly DateTimeOffset FridayUtc = new(2026, 7, 17, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SaturdayUtc = new(2026, 7, 18, 9, 0, 0, TimeSpan.Zero);

    // 段階を Stage 2（実弾最小）に据える。台帳は追記専用で現在段階は履歴の畳み込みなので、単一遷移で到達させる。
    private static void SeedStage(RiskWorkerWebApplicationFactory factory, TradingStage stage)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IStageGateStore>().Append(new StageTransition(
            1, TradingStage.Stage0Verification, stage, StageTransitionKind.Promotion,
            "seed", FridayUtc, "test seed"));
    }

    private static void SeedPerformance(RiskWorkerWebApplicationFactory factory, StagePerformance performance)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IStagePerformanceStore>().Save(performance);
    }

    // 実DD がバックテスト最大DD × 倍率を超える＝Stage 2/3 の撤退（自動停止）を発火させる実績。
    private static StagePerformance DrawdownBreach() => new()
    {
        BacktestMaxDrawdownRatio = 0.10m,
        ObservedMaxDrawdownRatio = 0.20m, // 0.10 × 1.5 = 0.15 を超過
    };

    private static WithdrawalEvaluationService BuildDriver(
        RiskWorkerWebApplicationFactory factory, DateTimeOffset now) =>
        new(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(now),
            new WeekendBusinessCalendar(),
            Options.Create(new WithdrawalEvaluationOptions()),
            NullLogger<WithdrawalEvaluationService>.Instance);

    private static bool KillSwitchEngaged(RiskWorkerWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<KillSwitchService>().GetState().Engaged;
    }

    [Fact]
    public async Task 撤退基準到達時に自動停止し_WithdrawalTriggered_を発行する()
    {
        // 受け入れ基準: 撤退基準到達時に kill switch 自動起動＋降格提案を通知（#15）。
        using var factory = new RiskWorkerWebApplicationFactory();
        factory.CreateClient(); // ホスト（＋ハーネスバス）を起動する。
        SeedStage(factory, TradingStage.Stage2MinimalLive);
        SeedPerformance(factory, DrawdownBreach());
        var harness = factory.Services.GetRequiredService<ITestHarness>();

        await BuildDriver(factory, FridayUtc).RunOnceAsync(CancellationToken.None);

        KillSwitchEngaged(factory).Should().BeTrue();
        (await harness.Published.Any<WithdrawalTriggered>(
            p => p.Context.Message.HaltNewEntries
                && p.Context.Message.ProposedStage == (int)TradingStage.Stage0Verification
                && p.Context.Message.Reason == nameof(WithdrawalReason.DrawdownBreachedMultiple)))
            .Should().BeTrue();
    }

    [Fact]
    public async Task 既に停止済みなら再発行しない_冪等()
    {
        // 受け入れ基準: 非発火時は副作用なし・冪等（既に kill switch 起動済みなら再起動しない＝再通知しない）。
        using var factory = new RiskWorkerWebApplicationFactory();
        factory.CreateClient();
        SeedStage(factory, TradingStage.Stage2MinimalLive);
        SeedPerformance(factory, DrawdownBreach());
        var harness = factory.Services.GetRequiredService<ITestHarness>();
        var driver = BuildDriver(factory, FridayUtc);

        await driver.RunOnceAsync(CancellationToken.None); // 1 回目: 新規停止 → 発行
        await driver.RunOnceAsync(CancellationToken.None); // 2 回目: 起動済み → 非発行

        (await harness.Published.Any<WithdrawalTriggered>()).Should().BeTrue();
        harness.Published.Select<WithdrawalTriggered>()
            .Should().ContainSingle("新規に停止した 1 回だけ通知する（撤退継続中の再通知はしない）");
    }

    [Fact]
    public async Task 休場日は評価をスキップし発火しない()
    {
        // 受け入れ基準: 市場休場ガード。土曜は評価せず、撤退基準を満たす実績でも自動停止・通知しない。
        using var factory = new RiskWorkerWebApplicationFactory();
        factory.CreateClient();
        SeedStage(factory, TradingStage.Stage2MinimalLive);
        SeedPerformance(factory, DrawdownBreach());
        var harness = factory.Services.GetRequiredService<ITestHarness>();

        await BuildDriver(factory, SaturdayUtc).RunOnceAsync(CancellationToken.None);

        KillSwitchEngaged(factory).Should().BeFalse();
        (await harness.Published.Any<WithdrawalTriggered>()).Should().BeFalse();
    }

    [Fact]
    public async Task 撤退基準に達していなければ副作用なし_非発行()
    {
        // 受け入れ基準: 実績未供給（既定・起点 Stage 0）は fail-safe で非発火＝停止も通知もしない。
        using var factory = new RiskWorkerWebApplicationFactory();
        factory.CreateClient();
        var harness = factory.Services.GetRequiredService<ITestHarness>();

        await BuildDriver(factory, FridayUtc).RunOnceAsync(CancellationToken.None);

        KillSwitchEngaged(factory).Should().BeFalse();
        (await harness.Published.Any<WithdrawalTriggered>()).Should().BeFalse();
    }

    // 休場ガードの当日判定を制御するための固定時計。
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;

        public DateOnly Today => DateOnly.FromDateTime(now.UtcDateTime);
    }
}
