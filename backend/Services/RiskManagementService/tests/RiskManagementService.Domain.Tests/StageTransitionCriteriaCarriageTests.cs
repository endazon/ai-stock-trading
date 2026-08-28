using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Domain.Tests;

// FR-20, FR-11, SC-02, UC-06, #466, 06_daytrading-review §4.1 追補3（2026-08-07・質問票 第15回 Q13-a）,
// IADR-0180: **遷移応答へ実効の合格条件を載せる契約変更**。
//
// 裁定は「`/stage promote`（承認操作）に警告を出す」と定めたが、`StageTransitionResult` は合格条件を
// 運んでいなかったため、Discord 側（承認の窓口）は警告を出すための材料を持てなかった。
//
// **判定はサーバ（Risk）が `BelowStatisticalBasis` で宣言する**——閾値 100 を表示側へ写経しない
// （IADR-0164 決定6 の規律）。本テスト群はその宣言が**遷移応答にも**載ることを固定する。
public class StageTransitionCriteriaCarriageTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly ControlViolationTally Clean = ControlViolationTally.Observed(0);

    private static StagePerformance Passing() => new()
    {
        BacktestPassed = true,
        BacktestMaxDrawdownRatio = 0.10m,
        ObservedMaxDrawdownRatio = 0.05m,
        Stage1QualifiedTradingDays = 60,
        Stage1TradeCount = 100,
        SlippageAndCostWithinExpected = true,
        DailyLossLimitRespected = true,
    };

    // 最小取引件数だけを差し替えた方針（`StageGateService.EffectivePolicy()` が本番で行う重ね方と同じ）。
    private static StageGatePolicy PolicyWithMinimumTradeCount(int minimumTradeCount)
    {
        var policy = TradingDefaults.CreateStagePolicy();
        return policy with
        {
            Stage1Criteria = policy.Stage1Criteria with { MinimumTradeCount = minimumTradeCount },
        };
    }

    // 受け入れ基準: 引き下げ状態なら、遷移応答が「警告の対象である」ことを宣言する。
    [Fact]
    public void 受理された昇格の応答が実効の最小取引件数と警告有無を運ぶ()
    {
        var approval = new StageApproval(TradingStage.Stage1Simulate, ApprovedBy: "endazon");

        var result = StageGate.RequestTransition(
            TradingStage.Stage0Verification, nextSequence: 1, approval, Passing(), Clean,
            PolicyWithMinimumTradeCount(5), Now);

        result.Accepted.Should().BeTrue();
        result.Stage1Criteria.Should().NotBeNull();
        result.Stage1Criteria!.MinimumTradeCount.Should().Be(5);
        result.Stage1Criteria.BelowStatisticalBasis.Should().BeTrue();
    }

    // **否定形**: 既定（100 件）のままなら警告の対象ではない（常時警告は「読まれない警告」になる）。
    [Fact]
    public void 既定の最小取引件数では警告の対象にならない()
    {
        var approval = new StageApproval(TradingStage.Stage1Simulate, ApprovedBy: "endazon");

        var result = StageGate.RequestTransition(
            TradingStage.Stage0Verification, nextSequence: 1, approval, Passing(), Clean,
            PolicyWithMinimumTradeCount(Stage1TradeCountBounds.Default), Now);

        result.Stage1Criteria.Should().NotBeNull();
        result.Stage1Criteria!.MinimumTradeCount.Should().Be(Stage1TradeCountBounds.Default);
        result.Stage1Criteria.BelowStatisticalBasis.Should().BeFalse();
    }

    // 決定1: **拒否された遷移にも載せる。** 拒否時も承認操作は行われており、設定が下がっている事実は
    // 変わらない。「拒否されたときだけ警告が消える」経路を作らない。
    [Theory]
    // 承認者が空（NoUserApproval）
    [InlineData("", 1, 0)]
    // 飛び級（PromotionMustBeSequential）
    [InlineData("endazon", 3, 0)]
    // 現段階指定（TargetIsCurrentStage）
    [InlineData("endazon", 0, 0)]
    public void 拒否された遷移の応答にも実効の合格条件が載る(string approvedBy, int target, int current)
    {
        var approval = new StageApproval((TradingStage)target, approvedBy);

        var result = StageGate.RequestTransition(
            (TradingStage)current, nextSequence: 1, approval, Passing(), Clean,
            PolicyWithMinimumTradeCount(5), Now);

        result.Accepted.Should().BeFalse();
        result.Stage1Criteria.Should().NotBeNull();
        result.Stage1Criteria!.MinimumTradeCount.Should().Be(5);
        result.Stage1Criteria.BelowStatisticalBasis.Should().BeTrue();
    }

    // 合格基準の未充足で拒否された昇格にも載る（Discord は 422 の本文からも警告を出せる）。
    [Fact]
    public void 合格基準未充足で拒否された昇格の応答にも実効の合格条件が載る()
    {
        var approval = new StageApproval(TradingStage.Stage1Simulate, ApprovedBy: "endazon");
        var failing = Passing() with { BacktestPassed = false };

        var result = StageGate.RequestTransition(
            TradingStage.Stage0Verification, nextSequence: 1, approval, failing, Clean,
            PolicyWithMinimumTradeCount(5), Now);

        result.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(StageGateCriterion.BacktestNotPassed);
        result.Stage1Criteria!.BelowStatisticalBasis.Should().BeTrue();
    }

    // 🔴 **警告を理由に昇格を拒否しない。** 裁定は「警告を伴う利用者の明示的な選択として認める」と
    // 定めている。件数を 5 件へ下げた状態で実績 5 件でも、条件 3 は充足として昇格が受理される。
    //
    // このテストは #466 の実装が「警告を出すついでに拒否する」方向へ倒れていないことの実測である。
    [Fact]
    public void 警告が出ていても昇格そのものは拒否されない()
    {
        var approval = new StageApproval(TradingStage.Stage2MinimalLive, ApprovedBy: "endazon");
        var performance = Passing() with { Stage1TradeCount = 5, Stage1QualifiedTradingDays = 60 };

        var result = StageGate.RequestTransition(
            TradingStage.Stage1Simulate, nextSequence: 2, approval, performance, Clean,
            PolicyWithMinimumTradeCount(5), Now);

        result.Stage1Criteria!.BelowStatisticalBasis.Should().BeTrue();
        result.Accepted.Should().BeTrue("警告は設定を妨げず、昇格も妨げない（§4.1 追補3）");
        result.RejectionReasons.Should().BeEmpty();
    }
}
