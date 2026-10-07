using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-16, 04_report-templates 数値定義, IADR-0025: 損益集計（実現損益・費用・税・評価損益）を検証する。
public class PnlAggregatorTests
{
    private const decimal TaxRate = 0.20315m;

    // 諸費用（SEC・TAF）は既定で 0 にして、各テストが見る項だけを動かす（諸費用の算入は専用のテストで見る）。
    private static TradingAssumptions Assumptions(
        CommissionSchedule? commission = null, decimal fx = 0m, UsSellRegulatoryFeeSchedule? regulatory = null) => new()
        {
            UnitedStatesSellRegulatoryFees = regulatory ?? new UsSellRegulatoryFeeSchedule(0m, 0m, 0m),
            CapitalGainsTaxRate = TaxRate,
            JapanCommission = commission ?? new CommissionSchedule(0m, 0m, 0m),
            UnitedStatesCommission = commission ?? new CommissionSchedule(0m, 0m, 0m),
            FxSpreadRatio = fx,
            MinimumExpectedProfitMultiple = 1.5m,
            CostLimits = new MonthlyCostLimits(20_000m, 15_000m, 5_000m, 0m),
        };

    private static PeriodTradeFill Fill(
        TradeSide side, PositionEffect effect, int qty, decimal price, int minute,
        string sym = "AAPL", Market market = Market.UnitedStates) =>
        new(sym, market, side, effect, qty, price, new DateTimeOffset(2026, 7, 10, 0, minute, 0, TimeSpan.Zero));

    [Fact]
    public void 利益決済は実現損益と源泉徴収税と税引後を定義どおり算出する()
    {
        var fills = new[]
        {
            Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, 0),
            Fill(TradeSide.Sell, PositionEffect.Close, 10, 1_200m, 1),
        };

        var s = PnlAggregator.Aggregate(fills, Assumptions());

        s.RealizedPnlGross.Should().Be(2_000m);      // (1200-1000)*10
        s.TotalCost.Should().Be(0m);                 // 手数料/為替 未登録
        s.TaxWithheld.Should().Be(2_000m * TaxRate); // 利益に課税
        s.RealizedPnlNet.Should().Be(2_000m - 2_000m * TaxRate);
        s.RealizingTradeCount.Should().Be(1);
        s.TradeCount.Should().Be(2);
    }

    [Fact]
    public void 損失決済は源泉徴収税ゼロで税引後は税引前マイナス費用()
    {
        var fills = new[]
        {
            Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, 0),
            Fill(TradeSide.Sell, PositionEffect.Close, 10, 900m, 1),
        };

        var s = PnlAggregator.Aggregate(fills, Assumptions());

        s.RealizedPnlGross.Should().Be(-1_000m);
        s.TaxWithheld.Should().Be(0m);           // 損失には課税しない
        s.RealizedPnlNet.Should().Be(-1_000m);   // 費用0
    }

    // ⚠️ **期待を変更した（2026-10-07・#1201・計画 ADR-0035 決定 4・IADR-0501）。** 本テストは
    // 「費用合計は手数料と為替スプレッドを含む」（66）を固定していた。計画は為替スプレッドを**入出金時に一度だけ**計上し、
    // 約定ごとの「為替スプレッド相当」は**事前見積りに限る**と裁定した。**最初からこうだったのではない。**
    [Fact]
    public void 実現損益の控除項は手数料を含み約定ごとの為替スプレッドを含まない()
    {
        // 手数料 0.1%、為替スプレッド 0.2%（事前見積りの率。事後集計には乗らない）。
        var a = Assumptions(commission: new CommissionSchedule(0.001m, 0m, 0m), fx: 0.002m);
        var fills = new[]
        {
            // notional 10,000 → 手数料10（為替 20 は乗せない）
            Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, 0, sym: "7203", market: Market.Japan),
            // notional 12,000 → 手数料12（為替 24 は乗せない）
            Fill(TradeSide.Sell, PositionEffect.Close, 10, 1_200m, 1, sym: "7203", market: Market.Japan),
        };

        var s = PnlAggregator.Aggregate(fills, a);

        s.TotalCost.Should().Be(22m);
        s.RealizedPnlGross.Should().Be(2_000m);
        s.TaxWithheld.Should().Be((2_000m - 22m) * TaxRate);
        s.RealizedPnlNet.Should().Be(2_000m - 22m - (2_000m - 22m) * TaxRate);
    }

    // FR-06, FR-16, FR-17, 計画 ADR-0035 決定 5, 05_trading-assumptions §2, #1201: 取引諸費用（SEC・TAF）は
    // **米国株の売り約定だけ**に掛かり、実現損益の控除項（＝税の課税標準の控除）に入る。
    [Fact]
    public void 実現損益の控除項は米国株の売りの取引諸費用を含む()
    {
        var a = Assumptions(regulatory: TradingAssumptionsDefaults.UnitedStatesSellRegulatoryFees);
        var fills = new[]
        {
            Fill(TradeSide.Buy, PositionEffect.Open, 100_000, 10m, 0),   // 買い: 諸費用 0
            Fill(TradeSide.Sell, PositionEffect.Close, 100_000, 12m, 1), // 売り 1,200,000: SEC 24.72・TAF 16.6 → 上限 8.30
        };

        var s = PnlAggregator.Aggregate(fills, a);

        s.TotalCost.Should().Be(24.72m + 8.30m);
        s.TaxWithheld.Should().Be((200_000m - 33.02m) * TaxRate);
        s.RealizedPnlNet.Should().Be(200_000m - 33.02m - (200_000m - 33.02m) * TaxRate);
    }

    [Fact]
    public void 評価損益は現在値から算出し現在値の無い建玉はゼロ()
    {
        var fills = new[] { Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, 0) }; // 建玉のみ（未決済）
        var prices = new Dictionary<string, decimal> { ["AAPL"] = 1_100m };

        var s = PnlAggregator.Aggregate(fills, Assumptions(), prices);

        s.UnrealizedPnl.Should().Be(1_000m); // (1100-1000)*10
        s.RealizedPnlGross.Should().Be(0m);

        // 現在値なしなら 0。
        PnlAggregator.Aggregate(fills, Assumptions()).UnrealizedPnl.Should().Be(0m);
    }

    [Fact]
    public void ショートは値下がりで利益になる()
    {
        var fills = new[]
        {
            Fill(TradeSide.Sell, PositionEffect.Open, 10, 1_000m, 0),
            Fill(TradeSide.Buy, PositionEffect.Close, 10, 800m, 1),
        };

        PnlAggregator.Aggregate(fills, Assumptions()).RealizedPnlGross.Should().Be(2_000m);
    }

    // T-16-006 (#892, IADR-0381): 🔴 **手仕舞い（Close）が期間の在庫を超える分は「反転」ではない。**
    //
    // 是正前はここで `SignedInventory.Apply` の反転分岐が働き、余りを**新しいショート建玉**にしていた
    //（旧テスト名「反転_ロングからショート_は決済分の実現損益と残ショートの評価損益を扱う」）。
    // しかし手仕舞いは**台帳の建玉を減らす約定**であり、決済数量は保有数（全量）から決まる
    //（`PositionEffectResolver`。ゼロを跨ぐ分割は起きない）。報告書の在庫が足りないのは
    // **期間より前に建てた建玉があるから**であって、反転したからではない。
    // 余りを建てると**幻のショート**が開き、実在しない建玉の評価損益が §1 に出る（#892 の主訴）。
    [Fact]
    public void 手仕舞いが期間の在庫を超える分は幻のショートにせず算定できない決済として数える()
    {
        // 期間の在庫はロング 10 株。手仕舞い 15 株のうち 10 株は当期間で建てた玉、5 株は期間より前の玉である。
        var fills = new[]
        {
            Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, 0),
            Fill(TradeSide.Sell, PositionEffect.Close, 15, 1_200m, 1),
        };
        var prices = new Dictionary<string, decimal> { ["AAPL"] = 1_100m };

        var s = PnlAggregator.Aggregate(fills, Assumptions(), prices);

        s.RealizedPnlGross.Should().Be(2_000m);       // 賄えた 10 株ぶんだけ (1200-1000)*10
        s.RealizingTradeCount.Should().Be(1);
        s.UnvaluedSettlementCount.Should().Be(1);     // 賄えなかった 5 株を持つ約定が 1 件
        s.UnrealizedPnl.Should().Be(0m);              // 🔴 幻のショートを建てない（是正前は +500 が出ていた）
    }

    [Fact]
    public void 空の約定列はゼロサマリを返す()
    {
        var s = PnlAggregator.Aggregate(Array.Empty<PeriodTradeFill>(), Assumptions());
        s.RealizedPnlGross.Should().Be(0m);
        s.TotalCost.Should().Be(0m);
        s.TaxWithheld.Should().Be(0m);
        s.TradeCount.Should().Be(0);
    }
}
