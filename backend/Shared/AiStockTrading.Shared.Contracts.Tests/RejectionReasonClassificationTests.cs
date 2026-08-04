using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// FR-10, FR-20, UC-06, ADR-0016 決定10, #329 第 2 段階: 拒否理由のクラス分類。
// 段階ゲート（Stage 1→2）の合格条件「統制違反 0 件」が数える対象は**クラス C 限定**である
//（project-planning#58 の裁定・06_daytrading-review §4.1）。分類が実際に分かれていることを固定する。
public class RejectionReasonClassificationTests
{
    // 06_daytrading-review §4.1: 統制違反＝クラス C の拒否理由（BannedSymbol / ManipulativeOrderPattern）
    // を含む発注拒否。**限定列挙**であり、増やしてはならない。
    [Fact]
    public void クラスCは禁止銘柄と相場操縦パターンの2種に限られる()
    {
        var classC = Enum.GetValues<RejectionReason>()
            .Where(r => RejectionReasonClassification.ClassOf(r) == RejectionReasonClass.C);

        classC.Should().BeEquivalentTo(
            [RejectionReason.BannedSymbol, RejectionReason.ManipulativeOrderPattern]);
    }

    // ADR-0016 決定10:「7 種すべてクラス A（統制が設計どおり作動した記録）に分類する」。
    [Theory]
    [InlineData(RejectionReason.ShortSellDisabled)]
    [InlineData(RejectionReason.BorrowUnavailable)]
    [InlineData(RejectionReason.BorrowCostExceeded)]
    [InlineData(RejectionReason.ShortExposureExceeded)]
    [InlineData(RejectionReason.MaintenanceMarginBreach)]
    [InlineData(RejectionReason.DividendRecordDateNear)]
    [InlineData(RejectionReason.ShortPriceFloorBreach)]
    // FR-10 の逆指値同時発注（ADR-0016 決定2(b)）に対応する実装追加分。同じくクラス A（IADR-0131 決定3）。
    [InlineData(RejectionReason.StopOrderRequired)]
    public void 空売りの拒否理由はクラスAであり統制違反に計上しない(RejectionReason reason)
    {
        RejectionReasonClassification.ClassOf(reason).Should().Be(RejectionReasonClass.A);
        RejectionReasonClassification.CountsAsControlViolation(reason).Should().BeFalse();
    }

    // 06_daytrading-review §4.1: クラス A（統制の正常作動）とクラス B（緊急停止中・段階制約による拒否）。
    [Theory]
    [InlineData(RejectionReason.PerOrderAmountExceeded, RejectionReasonClass.A)]
    [InlineData(RejectionReason.DailyOrderAmountExceeded, RejectionReasonClass.A)]
    [InlineData(RejectionReason.MaxPositionsExceeded, RejectionReasonClass.A)]
    [InlineData(RejectionReason.DailyLossLimitReached, RejectionReasonClass.A)]
    [InlineData(RejectionReason.MaxDrawdownReached, RejectionReasonClass.A)]
    [InlineData(RejectionReason.SameDayReentry, RejectionReasonClass.A)]
    [InlineData(RejectionReason.KillSwitchActive, RejectionReasonClass.B)]
    [InlineData(RejectionReason.TradingPaused, RejectionReasonClass.B)]
    [InlineData(RejectionReason.StageProhibitsLiveTrading, RejectionReasonClass.B)]
    [InlineData(RejectionReason.StageCapitalCapExceeded, RejectionReasonClass.B)]
    [InlineData(RejectionReason.ProductTypeDisabled, RejectionReasonClass.B)]
    [InlineData(RejectionReason.MarketDisabled, RejectionReasonClass.B)]
    public void 上限超過と停止中の拒否はクラスAとBに分かれる(
        RejectionReason reason, RejectionReasonClass expected)
    {
        RejectionReasonClassification.ClassOf(reason).Should().Be(expected);
        RejectionReasonClassification.CountsAsControlViolation(reason).Should().BeFalse();
    }

    // 06_daytrading-review §4.1:「計上単位は 1 回の発注拒否につき 1 件」（複数理由でも 1 件）。
    // クラス C を 1 つでも含めば計上し、含まなければ計上しない。
    [Fact]
    public void 一回の拒否はクラスCを含むときだけ統制違反として数える()
    {
        RejectionReasonClassification.CountsAsControlViolation(
            [RejectionReason.PerOrderAmountExceeded, RejectionReason.BannedSymbol]).Should().BeTrue();
        RejectionReasonClassification.CountsAsControlViolation(
            [RejectionReason.BannedSymbol, RejectionReason.ManipulativeOrderPattern]).Should().BeTrue();
        RejectionReasonClassification.CountsAsControlViolation(
            [RejectionReason.ShortPriceFloorBreach, RejectionReason.BorrowUnavailable,
             RejectionReason.KillSwitchActive]).Should().BeFalse();
        RejectionReasonClassification.CountsAsControlViolation([]).Should().BeFalse();
    }

    // **否定形**: 空売りの拒否理由をクラス C 側へ寄せる（BannedSymbol に混ぜる）迂回が塞がれていること。
    // 市況由来の事象を「AI が禁止事項を犯そうとした件数」に混入させると、段階昇格ゲートが機能しなくなる
    //（ADR-0016 決定10）。空売り理由だけの拒否は、何件積み上げても統制違反 0 件のままである。
    [Fact]
    public void 空売り理由だけの拒否はいくつ重なっても統制違反にならない()
    {
        RejectionReason[] allShortSellReasons =
        [
            RejectionReason.ShortSellDisabled,
            RejectionReason.BorrowUnavailable,
            RejectionReason.BorrowCostExceeded,
            RejectionReason.ShortExposureExceeded,
            RejectionReason.MaintenanceMarginBreach,
            RejectionReason.DividendRecordDateNear,
            RejectionReason.ShortPriceFloorBreach,
            RejectionReason.StopOrderRequired,
        ];

        RejectionReasonClassification.CountsAsControlViolation(allShortSellReasons).Should().BeFalse();
        allShortSellReasons.Should().OnlyContain(
            r => RejectionReasonClassification.ClassOf(r) == RejectionReasonClass.A);
    }

    // 分類は**全理由を網羅**する（未分類が既定でクラス C 側へ落ちない）。
    [Fact]
    public void すべての拒否理由がいずれかのクラスに分類される()
    {
        var reasons = Enum.GetValues<RejectionReason>();

        reasons.Should().OnlyContain(r => Enum.IsDefined(RejectionReasonClassification.ClassOf(r)));
        reasons.Count(r => RejectionReasonClassification.ClassOf(r) == RejectionReasonClass.C)
            .Should().Be(2, "クラス C は限定列挙であり、新しい理由が既定で混入してはならない");
    }
}
