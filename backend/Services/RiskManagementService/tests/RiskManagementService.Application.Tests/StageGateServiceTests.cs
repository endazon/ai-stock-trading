using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-20, FR-15, UC-06, ADR-0008, IADR-0070: 段階ゲート遷移サービスの結線を検証する。
// 承認ゲート・履歴永続化・fail-safe 昇格・撤退の自動安全側（kill switch 起動）を受け入れ基準へ写像する。
public class StageGateServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 9, 0, 0, TimeSpan.Zero);

    private static (StageGateService svc, InMemoryStageGateStore ledger, InMemoryStagePerformanceStore perf, KillSwitchService kill)
        Build(TradingStage initial = TradingStage.Stage0Verification)
    {
        var ledger = new InMemoryStageGateStore(initial);
        var perf = new InMemoryStagePerformanceStore();
        var killStore = new InMemoryKillSwitchStore();
        var clock = new FakeClock(Now, DateOnly.FromDateTime(Now.DateTime));
        var kill = new KillSwitchService(killStore, new InMemorySettingsChangeLog(), clock);
        var svc = new StageGateService(ledger, perf, TradingDefaults.CreateStagePolicy(), kill, clock);
        return (svc, ledger, perf, kill);
    }

    [Fact]
    public void 承認者が空の遷移は拒否され台帳へ追記されない()
    {
        // 受け入れ基準: 承認なしに段階が遷移しない。
        var (svc, ledger, perf, _) = Build();
        perf.Save(new StagePerformance { BacktestPassed = true });

        var result = svc.RequestTransition(TradingStage.Stage1Simulate, approver: "  ");

        result.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(StageGateCriterion.NoUserApproval);
        ledger.Load().History.Should().BeEmpty();
    }

    [Fact]
    public void バックテスト未合格では_Stage0から1へ昇格できない_fail_safe既定()
    {
        // 受け入れ基準（fail-safe）: 段階別実績未記録（BacktestPassed=false）では昇格を許可しない。
        var (svc, ledger, _, _) = Build();

        var result = svc.RequestTransition(TradingStage.Stage1Simulate, approver: "owner");

        result.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(StageGateCriterion.BacktestNotPassed);
        ledger.Load().CurrentStage.Should().Be(TradingStage.Stage0Verification);
    }

    [Fact]
    public void バックテスト合格を記録すれば承認で昇格し履歴に残る()
    {
        // 受け入れ基準: バックテスト合格した戦略のみ Stage 1 へ進める／遷移履歴が監査できる。
        var (svc, ledger, perf, _) = Build();
        perf.Save(new StagePerformance { BacktestPassed = true });

        var result = svc.RequestTransition(TradingStage.Stage1Simulate, approver: "owner");

        result.Accepted.Should().BeTrue();
        result.ResultingSettings!.Mode.Should().Be(BrokerProvider.MoomooSimulate);
        var history = ledger.Load().History;
        history.Should().HaveCount(1);
        history[0].FromStage.Should().Be(TradingStage.Stage0Verification);
        history[0].ToStage.Should().Be(TradingStage.Stage1Simulate);
        history[0].Kind.Should().Be(StageTransitionKind.Promotion);
        history[0].ApprovedBy.Should().Be("owner");
        history[0].Sequence.Should().Be(1);
    }

    [Fact]
    public void 飛び級昇格は拒否される()
    {
        // Stage 0 → Stage 2 の飛び級は不可（1 段ずつ）。
        var (svc, _, perf, _) = Build();
        perf.Save(new StagePerformance { BacktestPassed = true });

        var result = svc.RequestTransition(TradingStage.Stage2MinimalLive, approver: "owner");

        result.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(StageGateCriterion.PromotionMustBeSequential);
    }

    [Fact]
    public void 差し戻し_降格_は承認のみで受理される()
    {
        // 差し戻し（段階を下げる方向）は安全側のため合格基準不問で承認受理（ADR-0008）。
        var (svc, ledger, _, _) = Build(TradingStage.Stage1Simulate);

        var result = svc.RequestTransition(TradingStage.Stage0Verification, approver: "owner");

        result.Accepted.Should().BeTrue();
        result.Transition!.Kind.Should().Be(StageTransitionKind.Demotion);
        ledger.Load().CurrentStage.Should().Be(TradingStage.Stage0Verification);
    }

    [Fact]
    public void 差し戻し受理で実DDの観測窓をリセットする()
    {
        // FR-20, ADR-0008, IADR-0103, #164: 実DD は単調非減少で累積するため、差し戻し（再検証のやり直し）で
        // 観測窓を区切らないと「撤退 → 降格 → 再昇格」の直後に過去の実DD で撤退が恒久的に再発火する。
        // 実DD 以外（backtest 由来・他の運用系）は温存する。
        var (svc, _, perf, _) = Build(TradingStage.Stage2MinimalLive);
        perf.Save(new StagePerformance
        {
            BacktestPassed = true,
            BacktestMaxDrawdownRatio = 0.10m,
            ObservedMaxDrawdownRatio = 0.20m,
            ControlViolationCount = 2,
        });

        var result = svc.RequestTransition(TradingStage.Stage1Simulate, approver: "owner");

        result.Accepted.Should().BeTrue();
        result.Transition!.Kind.Should().Be(StageTransitionKind.Demotion);
        var after = perf.GetCurrent();
        after.ObservedMaxDrawdownRatio.Should().Be(0m);
        after.BacktestPassed.Should().BeTrue();
        after.BacktestMaxDrawdownRatio.Should().Be(0.10m);
        after.ControlViolationCount.Should().Be(2);
    }

    [Fact]
    public void 昇格受理では実DDの観測窓を保持する()
    {
        // IADR-0103: 昇格側でリセットすると撤退の証拠を消して緩む。安全側（厳しい側）に倒し、観測は維持する。
        var (svc, _, perf, _) = Build();
        perf.Save(new StagePerformance { BacktestPassed = true, ObservedMaxDrawdownRatio = 0.20m });

        var result = svc.RequestTransition(TradingStage.Stage1Simulate, approver: "owner");

        result.Accepted.Should().BeTrue();
        perf.GetCurrent().ObservedMaxDrawdownRatio.Should().Be(0.20m);
    }

    [Fact]
    public void 受理されない遷移では実DDの観測窓を変更しない()
    {
        // IADR-0103: リセットは「受理された差し戻し」のみ。承認欠如で拒否された要求で観測が消えてはならない。
        var (svc, _, perf, _) = Build(TradingStage.Stage2MinimalLive);
        perf.Save(new StagePerformance { ObservedMaxDrawdownRatio = 0.20m });

        var result = svc.RequestTransition(TradingStage.Stage1Simulate, approver: "  ");

        result.Accepted.Should().BeFalse();
        perf.GetCurrent().ObservedMaxDrawdownRatio.Should().Be(0.20m);
    }

    [Fact]
    public void 実DDがバックテスト最大DDの倍率を超えると撤退で自動停止し降格提案する()
    {
        // 受け入れ基準: 差し戻し基準到達時に自動で安全側（停止・降格提案）に倒れる。
        var (svc, _, perf, kill) = Build(TradingStage.Stage2MinimalLive);
        perf.Save(new StagePerformance
        {
            BacktestMaxDrawdownRatio = 0.10m,
            ObservedMaxDrawdownRatio = 0.20m, // 0.10 × 1.5 = 0.15 を超過
        });

        var outcome = svc.EvaluateWithdrawal();

        outcome.Assessment.Triggered.Should().BeTrue();
        outcome.Assessment.HaltNewEntries.Should().BeTrue();
        outcome.Assessment.ProposedStage.Should().Be(TradingStage.Stage0Verification);
        // 自動で安全側＝kill switch が起動される（段階の実降格は行わない）。今回の呼び出しで新規に起動した。
        outcome.NewlyEngaged.Should().BeTrue();
        kill.GetState().Engaged.Should().BeTrue();
    }

    [Fact]
    public void 既に停止済みなら撤退評価は新規起動と判定しない_冪等()
    {
        // IADR-0083: 撤退が継続していても、既に起動済みなら NewlyEngaged=false（＝再通知の起点にならない）。
        var (svc, _, perf, _) = Build(TradingStage.Stage2MinimalLive);
        perf.Save(new StagePerformance
        {
            BacktestMaxDrawdownRatio = 0.10m,
            ObservedMaxDrawdownRatio = 0.20m,
        });

        svc.EvaluateWithdrawal().NewlyEngaged.Should().BeTrue(); // 1 回目: 新規起動
        svc.EvaluateWithdrawal().NewlyEngaged.Should().BeFalse(); // 2 回目: 起動済み
    }

    [Fact]
    public void 撤退基準に達していなければ自動停止しない()
    {
        // fail-safe 既定（実績なし・実DD 0）では Stage 2 の撤退は非発火＝kill switch は起動しない。
        var (svc, _, _, kill) = Build(TradingStage.Stage2MinimalLive);

        var outcome = svc.EvaluateWithdrawal();

        outcome.Assessment.Triggered.Should().BeFalse();
        outcome.NewlyEngaged.Should().BeFalse();
        kill.GetState().Engaged.Should().BeFalse();
    }

    [Fact]
    public void 現況は現段階の設定と昇格_撤退評価を返す()
    {
        var (svc, _, _, _) = Build();

        var status = svc.GetStatus();

        status.CurrentStage.Should().Be(TradingStage.Stage0Verification);
        status.CurrentSettings.Mode.Should().Be(BrokerProvider.InternalPaper);
        status.Promotion.TargetStage.Should().Be(TradingStage.Stage1Simulate);
        status.Promotion.Eligible.Should().BeFalse(); // 既定はバックテスト未合格
        status.History.Should().BeEmpty();
    }
}
