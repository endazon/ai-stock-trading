using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// FR-10, FR-11, FR-19, FR-20, ADR-0016 決定10, IADR-0134 決定2, #374:
// 拒否理由は HTTP 経路で**整数として**往来する（段階ゲートの `RejectionReasons`）。既存メンバの間へ
// 新しい理由を挿入すると、それ以降の序数がずれ、**過去に記録された拒否の意味が変わる**。
// 追加は常に末尾へ行う——本テストはその規律を機械的に固定する退行防止テストである。
public class RejectionReasonOrdinalStabilityTests
{
    // 2026-08-04 時点の序数（#374 で BuyInBanned を、#333 で段階別の商品種別強制 2 種を末尾へ追加した状態）。
    // **既存行の値を書き換えてはならない。** 新しい理由は末尾へ 1 行足すだけである。
    public static TheoryData<RejectionReason, int> FixedOrdinals { get; } = new()
    {
        { RejectionReason.KillSwitchActive, 0 },
        { RejectionReason.StageProhibitsLiveTrading, 1 },
        { RejectionReason.StageCapitalCapExceeded, 2 },
        { RejectionReason.ProductTypeDisabled, 3 },
        { RejectionReason.MarketDisabled, 4 },
        { RejectionReason.BannedSymbol, 5 },
        { RejectionReason.SameDayReentry, 6 },
        { RejectionReason.PerOrderAmountExceeded, 7 },
        { RejectionReason.DailyOrderAmountExceeded, 8 },
        { RejectionReason.MaxPositionsExceeded, 9 },
        { RejectionReason.DailyLossLimitReached, 10 },
        { RejectionReason.MaxDrawdownReached, 11 },
        { RejectionReason.ManipulativeOrderPattern, 12 },
        { RejectionReason.TradingPaused, 13 },
        { RejectionReason.ShortSellDisabled, 14 },
        { RejectionReason.BorrowUnavailable, 15 },
        { RejectionReason.BorrowCostExceeded, 16 },
        { RejectionReason.ShortExposureExceeded, 17 },
        { RejectionReason.MaintenanceMarginBreach, 18 },
        { RejectionReason.DividendRecordDateNear, 19 },
        { RejectionReason.ShortPriceFloorBreach, 20 },
        { RejectionReason.StopOrderRequired, 21 },
        { RejectionReason.BuyInBanned, 22 },
        { RejectionReason.StageProductTypeProhibited, 23 },
        { RejectionReason.StageShortSellReleaseUnmet, 24 },
        // FR-19, #375, ADR-0021 決定3/決定4, IADR-0153: 現金口座対応で追加した 3 種。**末尾へ追加**している。
        { RejectionReason.CashAccountSettlementHold, 25 },
        { RejectionReason.BrokerAccountTypeUnverified, 26 },
        { RejectionReason.GoodFaithViolationLimitReached, 27 },
    };

    [Theory]
    [MemberData(nameof(FixedOrdinals))]
    public void 拒否理由の序数は不変である(RejectionReason reason, int expectedOrdinal)
    {
        ((int)reason).Should().Be(
            expectedOrdinal,
            "拒否理由は整数として永続化・伝送される。既存メンバの間へ挿入すると過去の記録の意味が変わる"
                + "（新設は末尾へ追加する。IADR-0134 決定2）");
    }

    // **否定形**: 表に無いメンバ（＝末尾より前へ挿入された新設、または表の更新漏れ）を検出する。
    // 「末尾へ足す」規律は、足した本人が本表を更新して初めて成立する。
    [Fact]
    public void 序数表はすべての拒否理由を網羅する()
    {
        var tabulated = FixedOrdinals.Select(row => row.Data.Item1).ToArray();

        Enum.GetValues<RejectionReason>().Should().BeEquivalentTo(
            tabulated,
            "新しい拒否理由を追加したら本表へ 1 行足す。表に無いメンバは序数の固定から漏れる");
    }
}
