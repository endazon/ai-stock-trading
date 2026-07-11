using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Domain.Tests;

// FR-20, ADR-0008, UC-06: 段階ゲートの遷移管理（状態機械＋承認フロー・純ロジック）
// 受け入れ基準（Issue #20）:
// - 承認なしに段階が遷移しない。遷移履歴が監査できる
// - 差し戻し基準（撤退条件）到達時に自動で安全側（降格提案・停止）に倒れる
public class StageGateTests
{
    private static readonly StageGatePolicy Policy = TradingDefaults.CreateStagePolicy();
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 9, 0, 0, TimeSpan.Zero);

    // 全合格基準を満たす実績（各テストで必要分を上書きする）
    private static StagePerformance Passing() => new()
    {
        BacktestPassed = true,
        BacktestMaxDrawdownRatio = 0.10m,
        ObservedMaxDrawdownRatio = 0.05m,
        PaperDeviationExplained = true,
        ControlViolationCount = 0,
        SlippageAndCostWithinExpected = true,
        DailyLossLimitRespected = true,
    };

    // FR-20, ADR-0008: 承認なしに段階が遷移しない（承認者が空なら昇格は拒否される）
    [Fact]
    public void 承認者が空の昇格要求は拒否される()
    {
        var approval = new StageApproval(TradingStage.Stage1Paper, ApprovedBy: "");

        var result = StageGate.RequestTransition(
            TradingStage.Stage0Verification, nextSequence: 1, approval, Passing(), Policy, Now);

        result.Accepted.Should().BeFalse();
        result.Transition.Should().BeNull();
        result.RejectionReasons.Should().Contain(StageGateCriterion.NoUserApproval);
    }

    // FR-20: 承認あり＋合格基準充足で昇格が受理され、遷移履歴と新設定が返る
    [Fact]
    public void 承認と合格基準充足でStage0からStage1へ昇格が受理される()
    {
        var approval = new StageApproval(TradingStage.Stage1Paper, ApprovedBy: "endazon");

        var result = StageGate.RequestTransition(
            TradingStage.Stage0Verification, nextSequence: 1, approval, Passing(), Policy, Now);

        result.Accepted.Should().BeTrue();
        result.RejectionReasons.Should().BeEmpty();
        result.Transition.Should().NotBeNull();
        result.Transition!.Sequence.Should().Be(1);
        result.Transition.FromStage.Should().Be(TradingStage.Stage0Verification);
        result.Transition.ToStage.Should().Be(TradingStage.Stage1Paper);
        result.Transition.Kind.Should().Be(StageTransitionKind.Promotion);
        result.Transition.ApprovedBy.Should().Be("endazon");
        result.Transition.OccurredAtUtc.Should().Be(Now);
        // Stage 1 はペーパー・資金上限は初期投入資金
        result.ResultingSettings.Should().Be(
            new StageSettings(TradingStage.Stage1Paper, TradeMode.Paper, TradingDefaults.InitialCapital));
    }

    // FR-20, FR-15: Stage 0→1 はバックテスト合格が前提。未合格なら承認があっても昇格は拒否される
    [Fact]
    public void バックテスト未合格ではStage0からStage1へ昇格できない()
    {
        var perf = Passing() with { BacktestPassed = false };
        var approval = new StageApproval(TradingStage.Stage1Paper, ApprovedBy: "endazon");

        var result = StageGate.RequestTransition(
            TradingStage.Stage0Verification, nextSequence: 1, approval, perf, Policy, Now);

        result.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(StageGateCriterion.BacktestNotPassed);
    }

    // FR-20: 昇格は 1 段ずつ。飛び級（Stage 0→2）は拒否される
    [Fact]
    public void 飛び級の昇格は拒否される()
    {
        var approval = new StageApproval(TradingStage.Stage2MinimalLive, ApprovedBy: "endazon");

        var result = StageGate.RequestTransition(
            TradingStage.Stage0Verification, nextSequence: 1, approval, Passing(), Policy, Now);

        result.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(StageGateCriterion.PromotionMustBeSequential);
    }

    // FR-20: Stage 1→2 は乖離が説明可能かつ統制違反 0 件が合格条件
    [Theory]
    [InlineData(false, 0, StageGateCriterion.PaperDeviationUnexplained)]
    [InlineData(true, 2, StageGateCriterion.ControlViolationsPresent)]
    public void Stage1からStage2は乖離説明と統制違反0が要る(
        bool deviationExplained, int violations, StageGateCriterion expected)
    {
        var perf = Passing() with
        {
            PaperDeviationExplained = deviationExplained,
            ControlViolationCount = violations,
        };
        var approval = new StageApproval(TradingStage.Stage2MinimalLive, ApprovedBy: "endazon");

        var result = StageGate.RequestTransition(
            TradingStage.Stage1Paper, nextSequence: 2, approval, perf, Policy, Now);

        result.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(expected);
    }

    // FR-20: Stage 2→3 はスリッページ・費用が想定内かつ日次損失上限の運用実績が合格条件
    [Theory]
    [InlineData(false, true, StageGateCriterion.SlippageOrCostExceeded)]
    [InlineData(true, false, StageGateCriterion.DailyLossLimitViolated)]
    public void Stage2からStage3はスリッページと日次損失実績が要る(
        bool slippageOk, bool dailyLossOk, StageGateCriterion expected)
    {
        var perf = Passing() with
        {
            SlippageAndCostWithinExpected = slippageOk,
            DailyLossLimitRespected = dailyLossOk,
        };
        var approval = new StageApproval(TradingStage.Stage3ScaledLive, ApprovedBy: "endazon");

        var result = StageGate.RequestTransition(
            TradingStage.Stage2MinimalLive, nextSequence: 3, approval, perf, Policy, Now);

        result.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(expected);
    }

    // FR-20: Stage 2→3 昇格が受理されると実弾モードの設定が返る
    [Fact]
    public void Stage2からStage3への昇格は実弾設定を返す()
    {
        var approval = new StageApproval(TradingStage.Stage3ScaledLive, ApprovedBy: "endazon");

        var result = StageGate.RequestTransition(
            TradingStage.Stage2MinimalLive, nextSequence: 3, approval, Passing(), Policy, Now);

        result.Accepted.Should().BeTrue();
        result.ResultingSettings!.Mode.Should().Be(TradeMode.Live);
        result.ResultingSettings.CapitalCap.Should().Be(TradingDefaults.InitialCapital);
    }

    // FR-20, ADR-0008: 差し戻し（段階を下げる方向）は安全側。承認があれば合格基準不問で受理される
    [Fact]
    public void 承認ありの差し戻しは合格基準不問で受理される()
    {
        // Stage 2 から再検証（Stage 0）への差し戻し。合格基準を満たさない実績でも受理される
        var perf = Passing() with { BacktestPassed = false, DailyLossLimitRespected = false };
        var approval = new StageApproval(TradingStage.Stage0Verification, ApprovedBy: "endazon");

        var result = StageGate.RequestTransition(
            TradingStage.Stage2MinimalLive, nextSequence: 4, approval, perf, Policy, Now);

        result.Accepted.Should().BeTrue();
        result.Transition!.Kind.Should().Be(StageTransitionKind.Demotion);
        result.Transition.ToStage.Should().Be(TradingStage.Stage0Verification);
        result.ResultingSettings.Should().Be(Policy.SettingsFor(TradingStage.Stage0Verification));
    }

    // FR-20: 遷移先が現段階と同じ要求は拒否される
    [Fact]
    public void 現段階と同じ遷移先は拒否される()
    {
        var approval = new StageApproval(TradingStage.Stage1Paper, ApprovedBy: "endazon");

        var result = StageGate.RequestTransition(
            TradingStage.Stage1Paper, nextSequence: 2, approval, Passing(), Policy, Now);

        result.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(StageGateCriterion.TargetIsCurrentStage);
    }

    // FR-20: 合格基準の評価（AssessPromotion）。充足時は昇格先と Eligible=true を返す
    [Fact]
    public void 合格基準充足時のAssessPromotionは昇格可能を返す()
    {
        var assessment = StageGate.AssessPromotion(TradingStage.Stage0Verification, Passing());

        assessment.Eligible.Should().BeTrue();
        assessment.TargetStage.Should().Be(TradingStage.Stage1Paper);
        assessment.UnmetCriteria.Should().BeEmpty();
    }

    // FR-20: 最上段（Stage 3）に昇格先はない
    [Fact]
    public void 最上段のAssessPromotionは昇格先なしを返す()
    {
        var assessment = StageGate.AssessPromotion(TradingStage.Stage3ScaledLive, Passing());

        assessment.Eligible.Should().BeFalse();
        assessment.TargetStage.Should().BeNull();
        assessment.UnmetCriteria.Should().Contain(StageGateCriterion.AlreadyAtTopStage);
    }

    // FR-20, IADR-0037: RequestTransition が返す遷移は台帳の追記整合（FromStage/Sequence）を満たし、
    // そのまま StageGateLedger へ追記できる（両純関数のシグネチャがドリフトしていないことのラウンドトリップ検証）。
    [Fact]
    public void 受理された遷移はそのまま台帳へ追記できる()
    {
        var ledger = StageGateLedger.Empty(TradingStage.Stage0Verification);
        var approval = new StageApproval(TradingStage.Stage1Paper, ApprovedBy: "endazon");

        // 台帳の現在段階・次シーケンスを入力に遷移を要求し、受理された遷移を台帳へ追記する
        var result = StageGate.RequestTransition(
            ledger.CurrentStage, ledger.NextSequence, approval, Passing(), Policy, Now);
        result.Accepted.Should().BeTrue();

        var appended = ledger.Append(result.Transition!); // 追記整合違反なら例外
        appended.CurrentStage.Should().Be(TradingStage.Stage1Paper);
        appended.NextSequence.Should().Be(2);
        appended.History.Should().ContainSingle().Which.Should().Be(result.Transition);
    }

    // FR-20, ADR-0008: 実弾段階で実DD がバックテスト最大DD の 1.5 倍以上 → 自動停止＋Stage 0 再検証提案
    [Fact]
    public void 実弾段階でDD超過は自動停止と再検証提案に倒れる()
    {
        // バックテスト最大DD 10%、実DD 15%（=10%×1.5）で到達
        var perf = Passing() with { BacktestMaxDrawdownRatio = 0.10m, ObservedMaxDrawdownRatio = 0.15m };

        var assessment = StageGate.AssessWithdrawal(TradingStage.Stage2MinimalLive, perf, Policy);

        assessment.Triggered.Should().BeTrue();
        assessment.Reason.Should().Be(WithdrawalReason.DrawdownBreachedMultiple);
        assessment.HaltNewEntries.Should().BeTrue();                         // 自動停止（安全側）
        assessment.ProposedStage.Should().Be(TradingStage.Stage0Verification); // 再検証への降格提案
    }

    // FR-20: 実弾段階でも DD が 1.5 倍未満なら撤退しない
    [Fact]
    public void 実弾段階でDDが倍率未満なら撤退しない()
    {
        var perf = Passing() with { BacktestMaxDrawdownRatio = 0.10m, ObservedMaxDrawdownRatio = 0.14m };

        var assessment = StageGate.AssessWithdrawal(TradingStage.Stage3ScaledLive, perf, Policy);

        assessment.Triggered.Should().BeFalse();
        assessment.HaltNewEntries.Should().BeFalse();
        assessment.ProposedStage.Should().BeNull();
    }

    // FR-20: バックテスト最大DD が未知（0）のときは倍率判定を行わず誤発火しない
    [Fact]
    public void バックテスト最大DDが0なら撤退を誤発火しない()
    {
        var perf = Passing() with { BacktestMaxDrawdownRatio = 0m, ObservedMaxDrawdownRatio = 0m };

        var assessment = StageGate.AssessWithdrawal(TradingStage.Stage2MinimalLive, perf, Policy);

        assessment.Triggered.Should().BeFalse();
    }

    // FR-20, 06_daytrading-review §4: ペーパー段階で乖離が説明不能 → Stage 0 へ差し戻し提案（停止は不要）
    [Fact]
    public void ペーパー段階の乖離説明不能は差し戻し提案に倒れる()
    {
        var perf = Passing() with { PaperDeviationExplained = false };

        var assessment = StageGate.AssessWithdrawal(TradingStage.Stage1Paper, perf, Policy);

        assessment.Triggered.Should().BeTrue();
        assessment.Reason.Should().Be(WithdrawalReason.PaperDeviationUnexplained);
        assessment.HaltNewEntries.Should().BeFalse();                        // ペーパーのため停止は不要
        assessment.ProposedStage.Should().Be(TradingStage.Stage0Verification);
    }

    // FR-20: 検証段階（Stage 0）は最下段のため撤退はない
    [Fact]
    public void 検証段階に撤退はない()
    {
        var perf = Passing() with { PaperDeviationExplained = false, ObservedMaxDrawdownRatio = 1m };

        var assessment = StageGate.AssessWithdrawal(TradingStage.Stage0Verification, perf, Policy);

        assessment.Triggered.Should().BeFalse();
    }
}
