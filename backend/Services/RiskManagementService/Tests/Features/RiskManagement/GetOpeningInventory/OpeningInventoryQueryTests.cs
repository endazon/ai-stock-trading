using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.GetOpeningInventory;
using Xunit;

namespace RiskManagementService.Tests;

// T-06-046〜T-06-048, FR-06, FR-16, #1181, IADR-0493 決定 1・2: 取引台帳を指定した市場の現地取引日より前（排他）まで畳む純関数。
public class OpeningInventoryQueryTests
{
    private static LedgerFill Fill(
        string symbol, Market market, TradeSide side, PositionEffect effect, int quantity, decimal price, DateTimeOffset at,
        decimal fxRateToBase = 1m, decimal? recognitionRate = 150m) =>
        new(symbol, market, side, effect, quantity, price, at, StopLossPrice: null, FxRateToBase: fxRateToBase,
            DecisionId: Guid.NewGuid(), Provider: BrokerProvider.MoomooSimulate, FxRateBaseToDisplay: recognitionRate);

    private static LedgerFill Adoption(string symbol, Market market, TradeSide side, int quantity, DateTimeOffset at) =>
        new(symbol, market, side, PositionEffect.Close, quantity, 0m, at, Origin: TradeOrigin.ManualAdoption);

    // ---- T-06-046: 境界は市場の現地取引日（排他）。窓の下端の取引日ちょうどの行は入らない ----

    [Fact]
    public void T06_046_米国は_ET_の取引日で切り_下端の取引日ちょうどの約定は入らない()
    {
        // EDT（UTC−4）。ET 2026-10-04 23:59 は UTC 10-05 03:59、ET 10-05 00:00 は UTC 10-05 04:00。
        var lastBefore = Fill("MSFT", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 10, 500m,
            new DateTimeOffset(2026, 10, 5, 3, 59, 0, TimeSpan.Zero));
        var onBoundary = Fill("MSFT", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 5, 600m,
            new DateTimeOffset(2026, 10, 5, 4, 0, 0, TimeSpan.Zero));

        var lots = OpeningInventoryQuery.AsOf([onBoundary, lastBefore], Market.UnitedStates, new DateOnly(2026, 10, 5));

        var lot = lots.Should().ContainSingle().Subject;
        lot.Quantity.Should().Be(10, "取引日 10-05 の約定は窓に入る（期間開始時点の在庫に二重に数えない）");
        lot.AverageCostInBase.Should().Be(500m);
    }

    [Fact]
    public void T06_046_東証は_JST_の取引日で切る_米国の行は市場が違えば入らない()
    {
        // JST 10-05 23:59 は UTC 10-05 14:59（取引日 10-05）、JST 10-06 00:00 は UTC 10-05 15:00（取引日 10-06）。
        var lastBefore = Fill("7203", Market.Japan, TradeSide.Buy, PositionEffect.Open, 100, 3000m,
            new DateTimeOffset(2026, 10, 5, 14, 59, 0, TimeSpan.Zero), fxRateToBase: 0.0067m);
        var onBoundary = Fill("7203", Market.Japan, TradeSide.Buy, PositionEffect.Open, 100, 3100m,
            new DateTimeOffset(2026, 10, 5, 15, 0, 0, TimeSpan.Zero), fxRateToBase: 0.0067m);
        var other = Fill("MSFT", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 1, 1m,
            new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero));

        var lots = OpeningInventoryQuery.AsOf([lastBefore, onBoundary, other], Market.Japan, new DateOnly(2026, 10, 6));

        var lot = lots.Should().ContainSingle().Subject;
        lot.Symbol.Should().Be("7203");
        lot.Market.Should().Be(Market.Japan);
        lot.Quantity.Should().Be(100);
        lot.AverageCostInBase.Should().Be(3000m * 0.0067m, "基準通貨（USD）の単価で畳む（報告書の約定単価と同じ通貨）");
    }

    // ---- T-06-047: 全決済は返さない・取り込みは数量だけ・ショートは向きで返す ----

    [Fact]
    public void T06_047_期間より前に全決済した建玉は返さず_取り込みは平均取得単価で数量だけ減らす()
    {
        var d = new DateTimeOffset(2026, 9, 28, 14, 0, 0, TimeSpan.Zero);
        var fills = new[]
        {
            // NVDA: 前に建てて前に全決済 → 返らない。
            Fill("NVDA", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 50, 200m, d),
            Fill("NVDA", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, 50, 210m, d.AddDays(1)),
            // AAPL: 100 @ 200 と 100 @ 230 → 平均 215。取り込みで 40 減らす（平均は不変）。
            Fill("AAPL", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 100, 200m, d),
            Fill("AAPL", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 100, 230m, d.AddDays(1)),
            Adoption("AAPL", Market.UnitedStates, TradeSide.Sell, 40, d.AddDays(2)),
            // TSLA: ショート 30 @ 250。
            Fill("TSLA", Market.UnitedStates, TradeSide.Sell, PositionEffect.Open, 30, 250m, d),
        };

        var lots = OpeningInventoryQuery.AsOf(fills, Market.UnitedStates, new DateOnly(2026, 10, 5));

        lots.Select(l => l.Symbol).Should().Equal(["AAPL", "TSLA"], "銘柄コードの序数順・全決済した NVDA は返らない");
        lots[0].Should().Be(new OpeningInventoryView("AAPL", Market.UnitedStates, TradeSide.Buy, 160, 215m, 150m, 0));
        lots[1].Should().Be(new OpeningInventoryView("TSLA", Market.UnitedStates, TradeSide.Sell, 30, 250m, 150m, 0));
    }

    [Fact]
    public void T06_047_約定時刻の順で畳む_入力順に依らない()
    {
        var d = new DateTimeOffset(2026, 9, 28, 14, 0, 0, TimeSpan.Zero);
        var buy = Fill("MSFT", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 10, 500m, d);
        var sell = Fill("MSFT", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, 4, 520m, d.AddHours(1));
        var add = Fill("MSFT", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 6, 530m, d.AddHours(2));

        var forward = OpeningInventoryQuery.AsOf([buy, sell, add], Market.UnitedStates, new DateOnly(2026, 10, 1));
        var shuffled = OpeningInventoryQuery.AsOf([add, buy, sell], Market.UnitedStates, new DateOnly(2026, 10, 1));

        // 10@500 → 4 売り（平均不変 500・残 6）→ 6@530 を建て増し: (6×500 + 6×530) / 12 = 515。
        forward.Should().Equal(shuffled);
        forward.Single().Quantity.Should().Be(12);
        forward.Single().AverageCostInBase.Should().Be(515m);
    }

    // ---- T-06-048: 認識時レートは基準通貨の原価で加重平均・未記録は null と件数 ----

    [Fact]
    public void T06_048_認識時レートは基準通貨の原価で加重平均し_一部決済では変わらない()
    {
        var d = new DateTimeOffset(2026, 9, 28, 14, 0, 0, TimeSpan.Zero);
        var fills = new[]
        {
            Fill("MSFT", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 10, 100m, d, recognitionRate: 150m),
            Fill("MSFT", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 10, 300m, d.AddHours(1), recognitionRate: 160m),
            Fill("MSFT", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, 5, 310m, d.AddHours(2), recognitionRate: 170m),
        };

        var lot = OpeningInventoryQuery.AsOf(fills, Market.UnitedStates, new DateOnly(2026, 10, 1)).Single();

        // (1,000×150 + 3,000×160) / 4,000 = 157.5。決済の約定のレートは平均に入らない。
        lot.AverageFxRateBaseToDisplay.Should().Be(157.5m);
        lot.UnrecordedFxRateFillCount.Should().Be(0);
        lot.Quantity.Should().Be(15);
        lot.AverageCostInBase.Should().Be(200m);
    }

    // T-06-054（独立監査 🟡3）: 向きが反転した建玉は、反転させた約定だけの建玉として数え直す（反転後の数量が反転前より
    // 小さい形と大きい形の両方。前者は一部決済の分岐へ、後者は建て増しの分岐へ誤って落ち得る）。
    [Theory]
    [InlineData(15, 160)]
    [InlineData(25, 160)]
    [InlineData(15, null)]
    [InlineData(25, null)]
    public void T06_054_反転した建玉は反転させた約定のレートと単価で数え直す(int sellQuantity, int? flipRate)
    {
        var d = new DateTimeOffset(2026, 9, 28, 14, 0, 0, TimeSpan.Zero);
        var fills = new[]
        {
            Fill("MSFT", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 10, 100m, d, recognitionRate: 150m),
            Fill("MSFT", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, sellQuantity, 120m, d.AddHours(1),
                recognitionRate: flipRate),
        };

        var lot = OpeningInventoryQuery.AsOf(fills, Market.UnitedStates, new DateOnly(2026, 10, 1)).Single();

        lot.Side.Should().Be(TradeSide.Sell);
        lot.Quantity.Should().Be(sellQuantity - 10);
        lot.AverageCostInBase.Should().Be(120m, "反転後の建玉は反転させた約定の単価で建つ");
        lot.AverageFxRateBaseToDisplay.Should().Be(flipRate, "反転前の建玉のレート（150）を引き継がない");
        lot.UnrecordedFxRateFillCount.Should().Be(flipRate is null ? 1 : 0);
    }

    [Fact]
    public void T06_048_未記録の約定が建玉に残れば_null_と件数を返し_全決済で数え直す()
    {
        var d = new DateTimeOffset(2026, 9, 28, 14, 0, 0, TimeSpan.Zero);
        var unknown = new[]
        {
            Fill("MSFT", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 10, 100m, d, recognitionRate: null),
            Fill("MSFT", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 10, 100m, d.AddHours(1), recognitionRate: 155m),
        };
        var reset = unknown.Concat(
        [
            Fill("MSFT", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, 20, 110m, d.AddHours(2)),
            Fill("MSFT", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 3, 120m, d.AddHours(3), recognitionRate: 149m),
        ]).ToArray();

        var held = OpeningInventoryQuery.AsOf(unknown, Market.UnitedStates, new DateOnly(2026, 10, 1)).Single();
        held.AverageFxRateBaseToDisplay.Should().BeNull("未記録の約定を含む建玉の平均は作れない（推定で埋めない）");
        held.UnrecordedFxRateFillCount.Should().Be(1);

        var fresh = OpeningInventoryQuery.AsOf(reset, Market.UnitedStates, new DateOnly(2026, 10, 1)).Single();
        fresh.AverageFxRateBaseToDisplay.Should().Be(149m, "全決済で建玉が消えたら次の建玉は数え直す");
        fresh.UnrecordedFxRateFillCount.Should().Be(0);
        fresh.Quantity.Should().Be(3);
    }
}
