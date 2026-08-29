using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;
using RiskManagementService.Domain;

namespace RiskManagementService.Tests;

// FR-20, ADR-0008, UC-06: 段階ゲートの遷移管理（状態機械＋承認フロー・純ロジック）
// 受け入れ基準（Issue #20）:
// - 承認なしに段階が遷移しない。遷移履歴が監査できる
// - 差し戻し基準（撤退条件）到達時に自動で安全側（降格提案・停止）に倒れる
public class StageGateTests
{
    private static readonly StageGatePolicy Policy = TradingDefaults.CreateStagePolicy();
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 9, 0, 0, TimeSpan.Zero);

    // FR-20, #387, IADR-0148: **供給された**統制違反件数 0 件（＝条件1 を満たす）。
    // 未供給（null）とは別物である——未供給は条件未充足として扱われ、昇格しない。
    private static readonly ControlViolationTally Clean = ControlViolationTally.Observed(0);

    // 全合格基準を満たす実績（各テストで必要分を上書きする）
    private static StagePerformance Passing() => new()
    {
        BacktestPassed = true,
        BacktestMaxDrawdownRatio = 0.10m,
        ObservedMaxDrawdownRatio = 0.05m,
        // FR-20, #333: Stage 1 の機械判定 3 条件（クラス C 違反 0 件 / 60 営業日 / 100 件）を満たす実績。
        Stage1QualifiedTradingDays = 60,
        Stage1TradeCount = 100,
        SlippageAndCostWithinExpected = true,
        DailyLossLimitRespected = true,
    };

    // FR-20, ADR-0008: 承認なしに段階が遷移しない（承認者が空なら昇格は拒否される）
    [Fact]
    public void 承認者が空の昇格要求は拒否される()
    {
        var approval = new StageApproval(TradingStage.Stage1Simulate, ApprovedBy: "");

        var result = StageGate.RequestTransition(
            TradingStage.Stage0Verification, nextSequence: 1, approval, Passing(), Clean, Policy, Now);

        result.Accepted.Should().BeFalse();
        result.Transition.Should().BeNull();
        result.RejectionReasons.Should().Contain(StageGateCriterion.NoUserApproval);
    }

    // FR-20: 承認あり＋合格基準充足で昇格が受理され、遷移履歴と新設定が返る
    [Fact]
    public void 承認と合格基準充足でStage0からStage1へ昇格が受理される()
    {
        var approval = new StageApproval(TradingStage.Stage1Simulate, ApprovedBy: "endazon");

        var result = StageGate.RequestTransition(
            TradingStage.Stage0Verification, nextSequence: 1, approval, Passing(), Clean, Policy, Now);

        result.Accepted.Should().BeTrue();
        result.RejectionReasons.Should().BeEmpty();
        result.Transition.Should().NotBeNull();
        result.Transition!.Sequence.Should().Be(1);
        result.Transition.FromStage.Should().Be(TradingStage.Stage0Verification);
        result.Transition.ToStage.Should().Be(TradingStage.Stage1Simulate);
        result.Transition.Kind.Should().Be(StageTransitionKind.Promotion);
        result.Transition.ApprovedBy.Should().Be("endazon");
        result.Transition.OccurredAtUtc.Should().Be(Now);
        // FR-20, #334: Stage 1 の既定発注先は moomoo SIMULATE・段階としての金額の絞りは無い
        result.ResultingSettings.Should().Be(
            new StageSettings(TradingStage.Stage1Simulate, BrokerProvider.MoomooSimulate, TradingDefaults.FullCapitalCapRatio));
    }

    // FR-20, FR-15: Stage 0→1 はバックテスト合格が前提。未合格なら承認があっても昇格は拒否される
    [Fact]
    public void バックテスト未合格ではStage0からStage1へ昇格できない()
    {
        var perf = Passing() with { BacktestPassed = false };
        var approval = new StageApproval(TradingStage.Stage1Simulate, ApprovedBy: "endazon");

        var result = StageGate.RequestTransition(
            TradingStage.Stage0Verification, nextSequence: 1, approval, perf, Clean, Policy, Now);

        result.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(StageGateCriterion.BacktestNotPassed);
    }

    // FR-20: 昇格は 1 段ずつ。飛び級（Stage 0→2）は拒否される
    [Fact]
    public void 飛び級の昇格は拒否される()
    {
        var approval = new StageApproval(TradingStage.Stage2MinimalLive, ApprovedBy: "endazon");

        var result = StageGate.RequestTransition(
            TradingStage.Stage0Verification, nextSequence: 1, approval, Passing(), Clean, Policy, Now);

        result.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(StageGateCriterion.PromotionMustBeSequential);
    }

    // FR-20, #333, 06_daytrading-review §4.1: Stage 1→2 の機械判定 3 条件
    // （クラス C 統制違反 0 件 / 実際に取引できた日数 60 営業日 / 取引件数 100 件）。
    [Theory]
    [InlineData(2, 60, 100, StageGateCriterion.ControlViolationsPresent)]
    [InlineData(0, 59, 100, StageGateCriterion.Stage1TradingDaysInsufficient)]
    [InlineData(0, 60, 99, StageGateCriterion.Stage1TradeCountInsufficient)]
    public void Stage1からStage2は統制違反0と期間と件数が要る(
        int violations, int tradingDays, int tradeCount, StageGateCriterion expected)
    {
        var perf = Passing() with
        {
            Stage1QualifiedTradingDays = tradingDays,
            Stage1TradeCount = tradeCount,
        };
        var approval = new StageApproval(TradingStage.Stage2MinimalLive, ApprovedBy: "endazon");

        var result = StageGate.RequestTransition(
            TradingStage.Stage1Simulate, nextSequence: 2, approval, perf,
            ControlViolationTally.Observed(violations), Policy, Now);

        result.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(expected);
    }

    // **否定形**（FR-20, #333, 06_daytrading-review §4.1, ADR-0016 決定10）:
    // 昇格判定に用いる「統制違反 0 件」は**クラス C 限定**である（`BannedSymbol` / `ManipulativeOrderPattern`）。
    // 空売りの拒否理由 9 種はすべて**クラス A**（統制が設計どおり作動した記録）であり、
    // いくら積み上がっても昇格を止めない。クラス A を数えるとゲートが恒久ブロックになる（§4.1 の理由）。
    [Fact]
    public void クラスAの拒否がいくら積み上がっても昇格は止まらない()
    {
        RejectionReason[] classA =
        [
            RejectionReason.ShortSellDisabled,
            RejectionReason.BorrowUnavailable,
            RejectionReason.BorrowCostExceeded,
            RejectionReason.ShortExposureExceeded,
            RejectionReason.MaintenanceMarginBreach,
            RejectionReason.DividendRecordDateNear,
            RejectionReason.ShortPriceFloorBreach,
            RejectionReason.StopOrderRequired,
            RejectionReason.BuyInBanned,
            RejectionReason.PerOrderAmountExceeded,
            RejectionReason.DailyLossLimitReached,
        ];

        // 100 回の拒否がすべてクラス A なら、計上される統制違反は 0 件である。
        var violations = Enumerable.Repeat(classA, 100)
            .Count(reasons => RejectionReasonClassification.CountsAsControlViolation(reasons));
        violations.Should().Be(0, "クラス A は「統制違反 0 件」の件数に影響しない");

        StageGate.AssessPromotion(
                TradingStage.Stage1Simulate, Passing(), ControlViolationTally.Observed(violations), Policy)
            .Eligible.Should().BeTrue("クラス A の拒否は段階昇格ゲートを止めない");
    }

    // FR-20, §4.1 条件 1: クラス C は 1 件でも昇格を止める。
    // **計上単位は 1 回の発注拒否につき 1 件**（1 回の拒否で複数理由が返っても 1 件）。
    [Theory]
    [InlineData(RejectionReason.BannedSymbol)]
    [InlineData(RejectionReason.ManipulativeOrderPattern)]
    public void クラスCの拒否が1件でもあれば昇格は止まる(RejectionReason classC)
    {
        // 1 回の拒否に複数理由（クラス A ＋ クラス C）が含まれても 1 件として数える。
        RejectionReason[] oneRejection = [RejectionReason.ShortSellDisabled, classC, RejectionReason.BorrowUnavailable];
        RejectionReasonClassification.CountsAsControlViolation(oneRejection).Should().BeTrue();

        var assessment = StageGate.AssessPromotion(
            TradingStage.Stage1Simulate, Passing(), ControlViolationTally.Observed(1), Policy);

        assessment.Eligible.Should().BeFalse();
        assessment.UnmetCriteria.Should().Contain(StageGateCriterion.ControlViolationsPresent);
    }

    // **否定形・本 issue の核心**（FR-20, #387, §4.1 条件1, IADR-0148）:
    // **統制違反件数の集計が供給されていなければ昇格しない。**
    //
    // 段階ゲートの他の入力（営業日数・取引件数）の 0 は「未充足＝昇格しない」に倒れるが、
    // 違反件数の 0 だけは「違反が無い＝条件充足」を意味する。#333 の実装では未供給が 0 と同じ形
    // （非 nullable の int）だったため、**#385 / #386 が期間・件数を供給した瞬間に条件1 が
    // 無条件で通る**状態だった。本テストはその状態を再現し、昇格しないことを固定する
    // （`Passing()` は既に 60 営業日・100 件を満たしている＝#385 / #386 の供給が揃った状態そのもの）。
    [Fact]
    public void 統制違反件数が未供給なら期間と件数が揃っていても昇格しない()
    {
        var perf = Passing();
        perf.Stage1Progress.QualifiedTradingDays.Should().Be(60, "#385 の供給が揃った状態を前提にする");
        perf.Stage1Progress.TradeCount.Should().Be(100, "#386 の供給が揃った状態を前提にする");

        var assessment = StageGate.AssessPromotion(
            TradingStage.Stage1Simulate, perf, controlViolations: null, Policy);

        assessment.Eligible.Should().BeFalse("集計の供給が無い状態を「違反 0 件」と読んではならない");
        assessment.UnmetCriteria.Should().Contain(StageGateCriterion.ControlViolationCountUnavailable);
        assessment.UnmetCriteria.Should().NotContain(
            StageGateCriterion.ControlViolationsPresent,
            "未供給は『違反があった』とは別の事由である（監査で取り違えない）");
    }

    // 同上を承認付きの遷移要求でも固定する（AssessPromotion だけを直しても RequestTransition が
    // 別経路で通るなら塞げていない）。**承認があっても未供給なら昇格しない。**
    [Fact]
    public void 承認があっても統制違反件数が未供給なら昇格は受理されない()
    {
        var approval = new StageApproval(TradingStage.Stage2MinimalLive, ApprovedBy: "endazon");

        var result = StageGate.RequestTransition(
            TradingStage.Stage1Simulate, nextSequence: 2, approval, Passing(),
            controlViolations: null, Policy, Now);

        result.Accepted.Should().BeFalse();
        result.Transition.Should().BeNull();
        result.RejectionReasons.Should().Contain(StageGateCriterion.ControlViolationCountUnavailable);
    }

    // 正の確認: **供給されたうえで 0 件**なら条件1 は満たされる（未供給を止める判定が、
    // 正常な合格まで止めていないこと＝過剰に厳しくしていないことの確認）。
    [Fact]
    public void 供給された0件は条件1を満たす()
    {
        var assessment = StageGate.AssessPromotion(
            TradingStage.Stage1Simulate, Passing(), ControlViolationTally.Observed(0), Policy);

        assessment.Eligible.Should().BeTrue();
        assessment.UnmetCriteria.Should().BeEmpty();
    }

    // **否定形**: 未供給は Stage 1 → 2 の条件であり、他段階の昇格を巻き添えで止めない
    // （§4.1 条件1 は Stage 1 の合格条件である）。
    [Theory]
    [InlineData(TradingStage.Stage0Verification)]
    [InlineData(TradingStage.Stage2MinimalLive)]
    public void 統制違反件数の未供給はStage1以外の昇格を止めない(TradingStage current)
    {
        StageGate.AssessPromotion(current, Passing(), controlViolations: null, Policy)
            .Eligible.Should().BeTrue();
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
            TradingStage.Stage2MinimalLive, nextSequence: 3, approval, perf, Clean, Policy, Now);

        result.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(expected);
    }

    // FR-20: Stage 2→3 昇格が受理されると実弾モードの設定が返る
    [Fact]
    public void Stage2からStage3への昇格は実弾設定を返す()
    {
        var approval = new StageApproval(TradingStage.Stage3ScaledLive, ApprovedBy: "endazon");

        var result = StageGate.RequestTransition(
            TradingStage.Stage2MinimalLive, nextSequence: 3, approval, Passing(), Clean, Policy, Now);

        result.Accepted.Should().BeTrue();
        result.ResultingSettings!.Mode.Should().Be(BrokerProvider.MoomooReal);
        result.ResultingSettings.CapitalCapRatio.Should().Be(TradingDefaults.FullCapitalCapRatio);
    }

    // FR-20, ADR-0008: 差し戻し（段階を下げる方向）は安全側。承認があれば合格基準不問で受理される
    [Fact]
    public void 承認ありの差し戻しは合格基準不問で受理される()
    {
        // Stage 2 から再検証（Stage 0）への差し戻し。合格基準を満たさない実績でも受理される
        var perf = Passing() with { BacktestPassed = false, DailyLossLimitRespected = false };
        var approval = new StageApproval(TradingStage.Stage0Verification, ApprovedBy: "endazon");

        var result = StageGate.RequestTransition(
            TradingStage.Stage2MinimalLive, nextSequence: 4, approval, perf, Clean, Policy, Now);

        result.Accepted.Should().BeTrue();
        result.Transition!.Kind.Should().Be(StageTransitionKind.Demotion);
        result.Transition.ToStage.Should().Be(TradingStage.Stage0Verification);
        result.ResultingSettings.Should().Be(Policy.SettingsFor(TradingStage.Stage0Verification));
    }

    // FR-20: 遷移先が現段階と同じ要求は拒否される
    [Fact]
    public void 現段階と同じ遷移先は拒否される()
    {
        var approval = new StageApproval(TradingStage.Stage1Simulate, ApprovedBy: "endazon");

        var result = StageGate.RequestTransition(
            TradingStage.Stage1Simulate, nextSequence: 2, approval, Passing(), Clean, Policy, Now);

        result.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(StageGateCriterion.TargetIsCurrentStage);
    }

    // FR-20: 合格基準の評価（AssessPromotion）。充足時は昇格先と Eligible=true を返す
    [Fact]
    public void 合格基準充足時のAssessPromotionは昇格可能を返す()
    {
        var assessment = StageGate.AssessPromotion(TradingStage.Stage0Verification, Passing(), Clean, Policy);

        assessment.Eligible.Should().BeTrue();
        assessment.TargetStage.Should().Be(TradingStage.Stage1Simulate);
        assessment.UnmetCriteria.Should().BeEmpty();
    }

    // FR-20: 最上段（Stage 3）に昇格先はない
    [Fact]
    public void 最上段のAssessPromotionは昇格先なしを返す()
    {
        var assessment = StageGate.AssessPromotion(TradingStage.Stage3ScaledLive, Passing(), Clean, Policy);

        assessment.Eligible.Should().BeFalse();
        assessment.TargetStage.Should().BeNull();
        assessment.UnmetCriteria.Should().Contain(StageGateCriterion.AlreadyAtTopStage);
    }

    // FR-20, IADR-0041: RequestTransition が返す遷移は台帳の追記整合（FromStage/Sequence）を満たし、
    // そのまま StageGateLedger へ追記できる（両純関数のシグネチャがドリフトしていないことのラウンドトリップ検証）。
    [Fact]
    public void 受理された遷移はそのまま台帳へ追記できる()
    {
        var ledger = StageGateLedger.Empty(TradingStage.Stage0Verification);
        var approval = new StageApproval(TradingStage.Stage1Simulate, ApprovedBy: "endazon");

        // 台帳の現在段階・次シーケンスを入力に遷移を要求し、受理された遷移を台帳へ追記する
        var result = StageGate.RequestTransition(
            ledger.CurrentStage, ledger.NextSequence, approval, Passing(), Clean, Policy, Now);
        result.Accepted.Should().BeTrue();

        var appended = ledger.Append(result.Transition!); // 追記整合違反なら例外
        appended.CurrentStage.Should().Be(TradingStage.Stage1Simulate);
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

    // FR-20, #333, 06_daytrading-review §4.3, INDEX 決定 42:
    // 累計 120 営業日を経ても件数が 100 件に届かなければ**打ち切り** → Stage 0 へ差し戻し提案（停止は不要）。
    [Fact]
    public void Stage1は120営業日で打ち切られ差し戻し提案に倒れる()
    {
        var perf = Passing() with { Stage1QualifiedTradingDays = 120, Stage1TradeCount = 99 };

        var assessment = StageGate.AssessWithdrawal(TradingStage.Stage1Simulate, perf, Policy);

        assessment.Triggered.Should().BeTrue();
        assessment.Reason.Should().Be(WithdrawalReason.Stage1ExtensionExhausted);
        assessment.HaltNewEntries.Should().BeFalse();                        // SIMULATE のため停止は不要
        assessment.ProposedStage.Should().Be(TradingStage.Stage0Verification);
    }

    // **否定形**（§4.3）: 期間を延長している間（60〜119 営業日）は打ち切らない。
    // 期間の要件は既に満たしているため再度 60 営業日を要さず、件数に達した時点で昇格判定を行う。
    [Theory]
    [InlineData(60)]
    [InlineData(119)]
    public void 延長中のStage1は打ち切らない(int tradingDays)
    {
        var perf = Passing() with { Stage1QualifiedTradingDays = tradingDays, Stage1TradeCount = 99 };

        StageGate.AssessWithdrawal(TradingStage.Stage1Simulate, perf, Policy)
            .Triggered.Should().BeFalse("期間の延長は 120 営業日まで許される");
    }

    // **否定形**（§4.3）: 120 営業日を超えていても**件数を満たしていれば打ち切らない**。
    // 打ち切り事由は「120 営業日を経ても 100 件に届かない」ことであり、期間の超過そのものではない。
    [Fact]
    public void 件数を満たしていれば120営業日を超えても打ち切らない()
    {
        var perf = Passing() with { Stage1QualifiedTradingDays = 121, Stage1TradeCount = 100 };

        StageGate.AssessWithdrawal(TradingStage.Stage1Simulate, perf, Policy)
            .Triggered.Should().BeFalse();
        StageGate.AssessPromotion(TradingStage.Stage1Simulate, perf, Clean, Policy)
            .Eligible.Should().BeTrue("期間・件数の両方を満たしているため昇格できる");
    }

    // FR-20: 検証段階（Stage 0）は最下段のため撤退はない
    [Fact]
    public void 検証段階に撤退はない()
    {
        var perf = Passing() with { Stage1QualifiedTradingDays = 120, Stage1TradeCount = 0, ObservedMaxDrawdownRatio = 1m };

        var assessment = StageGate.AssessWithdrawal(TradingStage.Stage0Verification, perf, Policy);

        assessment.Triggered.Should().BeFalse();
    }
}
