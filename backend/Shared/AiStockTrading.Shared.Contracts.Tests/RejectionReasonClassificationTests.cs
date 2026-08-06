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

    // ADR-0016 決定10:「9 種すべてクラス A（統制が設計どおり作動した記録）に分類する」
    //（2026-08-04 に 7 種から改訂。#374）。
    [Theory]
    [InlineData(RejectionReason.ShortSellDisabled)]
    [InlineData(RejectionReason.BorrowUnavailable)]
    [InlineData(RejectionReason.BorrowCostExceeded)]
    [InlineData(RejectionReason.ShortExposureExceeded)]
    [InlineData(RejectionReason.MaintenanceMarginBreach)]
    [InlineData(RejectionReason.DividendRecordDateNear)]
    [InlineData(RejectionReason.ShortPriceFloorBreach)]
    // 逆指値同時発注（決定2(b)）は #329 で実装が先行し、2026-08-04 に計画が同名で追認した。
    [InlineData(RejectionReason.StopOrderRequired)]
    // 強制買戻しの 30 日禁止（決定4）。計画が 2026-08-04 に新設したコード。
    [InlineData(RejectionReason.BuyInBanned)]
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
    [InlineData(RejectionReason.StageProductTypeProhibited, RejectionReasonClass.B)]
    [InlineData(RejectionReason.StageShortSellReleaseUnmet, RejectionReasonClass.B)]
    [InlineData(RejectionReason.ProductTypeDisabled, RejectionReasonClass.B)]
    [InlineData(RejectionReason.MarketDisabled, RejectionReasonClass.B)]
    // FR-19, #375, ADR-0021, IADR-0153 決定5: 現金口座対応の 3 種。
    // 決定4-5 が **CashAccountSettlementHold をクラス A** と明示している（制度・決済に由来する事象であり、
    // 「AI が禁止事項を犯そうとした」ものではない）。GoodFaithViolationLimitReached も統制の正常作動で A。
    // BrokerAccountTypeUnverified は**取引を止めている状態そのものの記録**であり、KillSwitch / Paused と同じ B。
    [InlineData(RejectionReason.CashAccountSettlementHold, RejectionReasonClass.A)]
    [InlineData(RejectionReason.GoodFaithViolationLimitReached, RejectionReasonClass.A)]
    [InlineData(RejectionReason.BrokerAccountTypeUnverified, RejectionReasonClass.B)]
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
            RejectionReason.StopOrderRequired,
            RejectionReason.BorrowUnavailable,
            RejectionReason.BorrowCostExceeded,
            RejectionReason.BuyInBanned,
            RejectionReason.ShortExposureExceeded,
            RejectionReason.MaintenanceMarginBreach,
            RejectionReason.DividendRecordDateNear,
            RejectionReason.ShortPriceFloorBreach,
        ];

        allShortSellReasons.Should().HaveCount(9, "ADR-0016 決定10 は 2026-08-04 に 9 種へ改訂された");
        RejectionReasonClassification.CountsAsControlViolation(allShortSellReasons).Should().BeFalse();
        allShortSellReasons.Should().OnlyContain(
            r => RejectionReasonClassification.ClassOf(r) == RejectionReasonClass.A);
    }

    // **否定形**（FR-19, #375, ADR-0021 決定4-5, IADR-0153 決定5）: 現金口座の統制群も同じ迂回が塞がれている。
    // 決済済み資金の不足・GFV 回数の到達・口座種別の未確認はいずれも**制度・決済・接続に由来する事象**であり、
    // 「AI が禁止事項を犯そうとした件数」（クラス C）へ混入させると段階昇格ゲートの「統制違反 0 件」が壊れる。
    // 現金口座は差金決済ガードの適用範囲が広い（米国株にも及ぶ）ため、混入したときの影響も大きい。
    [Fact]
    public void 現金口座の拒否理由だけの拒否はいくつ重なっても統制違反にならない()
    {
        RejectionReason[] cashAccountReasons =
        [
            RejectionReason.CashAccountSettlementHold,
            RejectionReason.GoodFaithViolationLimitReached,
            RejectionReason.BrokerAccountTypeUnverified,
            // 現金口座では差金決済ガードが米国株にも及ぶ（ADR-0021 決定4-1）。理由コードは従来どおりクラス A。
            RejectionReason.SameDayReentry,
        ];

        RejectionReasonClassification.CountsAsControlViolation(cashAccountReasons).Should().BeFalse();
        cashAccountReasons.Should().OnlyContain(
            r => RejectionReasonClassification.ClassOf(r) != RejectionReasonClass.C);
    }

    // **否定形**: 3 種は互いに別のコードである（一方を他方の別名にしてはならない）。
    // 解除条件が異なる——`CashAccountSettlementHold` は T+1 の決済で、`GoodFaithViolationLimitReached` は
    // 違反記録の失効で、`BrokerAccountTypeUnverified` は照会の成功で解ける（ADR-0016 決定10 と同じ規律）。
    [Fact]
    public void 現金口座の3種の拒否理由は互いに別のコードである()
    {
        var names = Enum.GetNames<RejectionReason>();
        names.Should().Contain("CashAccountSettlementHold");
        names.Should().Contain("GoodFaithViolationLimitReached");
        names.Should().Contain("BrokerAccountTypeUnverified");

        new[]
        {
            RejectionReason.CashAccountSettlementHold,
            RejectionReason.GoodFaithViolationLimitReached,
            RejectionReason.BrokerAccountTypeUnverified,
        }.Should().OnlyHaveUniqueItems();
    }

    // **否定形**（ADR-0016 決定10 の 2026-08-04 追記）: 強制買戻しの禁止（BuyInBanned）と借株不可
    //（BorrowUnavailable）は**別のコード**であり、一方を他方の別名にしてはならない。原因（期間の経過で
    // 解除される禁止 vs 都度の借株需給）も解除条件も異なるため、統合すると監査ログ（FR-11）の理由が
    // 実態と食い違い、日報・月報の「強制買戻しの発生有無・発生回数」（決定15）を復元できなくなる。
    [Fact]
    public void 強制買戻しの禁止と借株不可は別の拒否理由である()
    {
        RejectionReason.BuyInBanned.Should().NotBe(RejectionReason.BorrowUnavailable);
        Enum.GetNames<RejectionReason>().Should().Contain("BuyInBanned");

        // どちらもクラス A であり、区別は「統制違反 0 件」の計上には影響しない（分類ではなく記録の粒度の問題）。
        RejectionReasonClassification.ClassOf(RejectionReason.BuyInBanned)
            .Should().Be(RejectionReasonClass.A);
        RejectionReasonClassification.ClassOf(RejectionReason.BorrowUnavailable)
            .Should().Be(RejectionReasonClass.A);
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
