using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-16, #563, IADR-0268: 台帳の約定列と記録済みの判断根拠から日報 §2 の明細を組み立てる純関数の検証。
// **数値はコード集計値・文章は記録の転記**であり、いずれも報告書生成時に作られていないことを固定する。
public class TradeHistoryViewBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 28, 0, 5, 0, TimeSpan.Zero); // JST 09:05

    private static TradingAssumptions Assumptions() => TradingAssumptionsDefaults.Create();

    private static PeriodTradeFill Fill(
        TradeSide side, int quantity, decimal price, int minutes, Guid decisionId = default,
        Market market = Market.Japan, string symbol = "7203") =>
        new(symbol, market, side,
            side == TradeSide.Buy ? PositionEffect.Open : PositionEffect.Close,
            quantity, price, T0.AddMinutes(minutes), decisionId);

    // --- 境界値 ---

    [Fact]
    public void 約定が0件なら明細も0行になる()
    {
        var view = TradeHistoryViewBuilder.Build([], Assumptions(), rationales: null);

        view.Lines.Should().BeEmpty();
    }

    [Fact]
    public void 約定を約定時刻の昇順に並べ1から連番を振る()
    {
        // 入力は逆順（順序に依存しないこと）。
        var view = TradeHistoryViewBuilder.Build(
            [Fill(TradeSide.Sell, 100, 2_600m, 300), Fill(TradeSide.Buy, 100, 2_500m, 0)],
            Assumptions(), rationales: null);

        view.Lines.Select(l => l.Index).Should().Equal(1, 2);
        view.Lines.Select(l => l.Side).Should().Equal(TradeSide.Buy, TradeSide.Sell);
    }

    [Fact]
    public void 時刻をJSTで表す()
    {
        // T0 = 2026-08-28T00:05:00Z = JST 09:05。
        var view = TradeHistoryViewBuilder.Build([Fill(TradeSide.Buy, 100, 2_500m, 0)], Assumptions(), null);

        view.Lines.Should().ContainSingle().Which.Time.Should().Be(new TimeOnly(9, 5));
    }

    // --- 数値はコード集計値（LLM に計算させない） ---

    [Fact]
    public void 手数料と費用を前提条件の費用関数で積む()
    {
        var assumptions = Assumptions();
        var fill = Fill(TradeSide.Buy, 100, 2_500m, 0);

        var view = TradeHistoryViewBuilder.Build([fill], assumptions, null);

        view.Lines.Should().ContainSingle().Which.Cost
            .Should().Be(CostCalculator.EstimateOneWayCost(assumptions, Market.Japan, 100 * 2_500m));
    }

    [Fact]
    public void 在庫が減る約定にだけ実現損益を計上する()
    {
        var view = TradeHistoryViewBuilder.Build(
            [Fill(TradeSide.Buy, 100, 2_500m, 0), Fill(TradeSide.Sell, 100, 2_600m, 300)],
            Assumptions(), null);

        view.Lines[0].RealizedPnl.Should().Be(0m, "新規建ては実現しない（未供給ではなく事実としての 0）");
        view.Lines[1].RealizedPnl.Should().Be(10_000m, "(2,600 − 2,500) × 100");
    }

    // 🔴 **プロパティベース**: 明細の実現損益の総和は、§1 サマリ（PnlAggregator）の税引前実現損益と一致する。
    // **明細とサマリが食い違わない**ことが、明細を本文へ載せる前提である。
    [Theory]
    [InlineData(new[] { 10, -10 }, new[] { 100, 120 })]
    [InlineData(new[] { 10, 5, -12, -3 }, new[] { 100, 110, 130, 90 })]
    [InlineData(new[] { -10, 4, 6 }, new[] { 200, 180, 170 })]
    [InlineData(new[] { 5, -10, 5 }, new[] { 100, 150, 120 })]
    [InlineData(new[] { 3 }, new[] { 100 })]
    public void 明細の実現損益の総和はサマリの税引前実現損益と一致する(int[] signedQuantities, int[] prices)
    {
        var fills = signedQuantities
            .Select((q, i) => Fill(q > 0 ? TradeSide.Buy : TradeSide.Sell, Math.Abs(q), prices[i], i * 10))
            .ToList();

        var view = TradeHistoryViewBuilder.Build(fills, Assumptions(), null);
        var summary = PnlAggregator.Aggregate(fills, Assumptions());

        view.Lines.Sum(l => l.RealizedPnl).Should().Be(summary.RealizedPnlGross);
        view.Lines.Should().HaveCount(fills.Count, "1 約定 = 1 行（04_report-templates 日報 §2）");
    }

    // --- 判断根拠（記録の転記） ---

    // 🔴 **肯定形**: 記録がある約定は、記録された根拠が**そのまま**入る。
    [Fact]
    public void 記録された判断根拠をそのまま明細へ載せる()
    {
        var decisionId = Guid.NewGuid();
        var rationales = new Dictionary<Guid, string> { [decisionId] = "始値が支持線で反発。出来高増。" };

        var view = TradeHistoryViewBuilder.Build(
            [Fill(TradeSide.Buy, 100, 2_500m, 0, decisionId)], Assumptions(), rationales);

        view.Lines.Should().ContainSingle().Which.RationaleSummary
            .Should().Be("始値が支持線で反発。出来高増。");
    }

    // 🔴 **否定形（上の肯定形と対）**: 相関できない約定へ、別の記録を当てはめない。
    [Fact]
    public void 相関できない約定の判断根拠は未供給にする()
    {
        var rationales = new Dictionary<Guid, string> { [Guid.NewGuid()] = "別の判断の根拠。" };

        var view = TradeHistoryViewBuilder.Build(
            [
                Fill(TradeSide.Buy, 100, 2_500m, 0), // DecisionId 無し（Guid.Empty）
                Fill(TradeSide.Sell, 100, 2_600m, 300, Guid.NewGuid()), // 辞書に無い DecisionId
            ],
            Assumptions(), rationales);

        view.Lines.Should().OnlyContain(l => l.RationaleSummary == null);
    }

    [Fact]
    public void 判断記録そのものが未供給なら全行の判断根拠が未供給になる()
    {
        var view = TradeHistoryViewBuilder.Build(
            [Fill(TradeSide.Buy, 100, 2_500m, 0, Guid.NewGuid())], Assumptions(), rationales: null);

        view.Lines.Should().ContainSingle().Which.RationaleSummary.Should().BeNull();
    }

    [Fact]
    public void 空白だけの判断根拠は未供給として扱う()
    {
        var decisionId = Guid.NewGuid();
        var rationales = new Dictionary<Guid, string> { [decisionId] = "   " };

        var view = TradeHistoryViewBuilder.Build(
            [Fill(TradeSide.Buy, 100, 2_500m, 0, decisionId)], Assumptions(), rationales);

        view.Lines.Should().ContainSingle().Which.RationaleSummary.Should().BeNull();
    }

    // --- 記録源が無い項目（#563 受け入れ基準 2） ---

    // 🔴 **否定形**: 記録源が無い項目は既定値で埋めない。
    [Fact]
    public void 記録源が無い項目を既定値で埋めない()
    {
        var view = TradeHistoryViewBuilder.Build([Fill(TradeSide.Buy, 100, 2_500m, 0)], Assumptions(), null);

        var line = view.Lines.Should().ContainSingle().Subject;
        line.SymbolName.Should().BeNull("台帳が銘柄名を持たない");
        line.Tax.Should().NotBe(0m, "税 0 は「この約定に税が掛からなかった」と読める");
        line.Tax.Should().BeNull();
        line.Trigger.Should().NotBe(TradeTrigger.Scheduled, "起点を知らないことを「定時だった」と書かない");
        line.Trigger.Should().BeNull();
    }

    // 🔴 **肯定形（上の否定形と対）**: 引ける項目は必ず入る（全部を未供給にして逃げていない）。
    [Fact]
    public void 台帳から引ける項目は必ず入る()
    {
        var view = TradeHistoryViewBuilder.Build(
            [Fill(TradeSide.Sell, 10, 315m, 0, market: Market.UnitedStates, symbol: "AAPL")],
            Assumptions(), null);

        var line = view.Lines.Should().ContainSingle().Subject;
        line.SymbolCode.Should().Be("AAPL");
        line.Market.Should().Be(Market.UnitedStates);
        line.Side.Should().Be(TradeSide.Sell);
        line.Quantity.Should().Be(10);
        line.FillPrice.Should().Be(315m);
        // 米国市場は手数料無料かつ基準通貨のため概算費用は 0 になる（**未供給ではなく事実としての 0**）。
        line.Cost.Should().Be(CostCalculator.EstimateOneWayCost(Assumptions(), Market.UnitedStates, 10 * 315m));
    }

    // 取引詳細・見送り判断は記録源そのものが無い（空列＝「該当なし」へ倒さない）。
    [Fact]
    public void 取引詳細と見送り判断は未供給であり空列にしない()
    {
        var view = TradeHistoryViewBuilder.Build([Fill(TradeSide.Buy, 100, 2_500m, 0)], Assumptions(), null);

        view.Details.Should().BeNull();
        view.Skipped.Should().BeNull();
    }
}
