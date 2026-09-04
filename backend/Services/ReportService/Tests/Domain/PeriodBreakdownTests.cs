using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-07, FR-16, #615, IADR-0306, 04_report-templates 月報 §2「週別・市場別の内訳」:
// **週別・市場別・建玉方向別の内訳**（純関数）。
//
// 🔴 **3 表それぞれの和が §1 サマリと一致することを固定する。** 一致しない壊れ方は例外も赤いテストも
// 出さない——各スライスは自分の中では整合しているためである（IADR-0301 と同じ理由）。
public class PeriodBreakdownTests
{
    private static readonly TradingAssumptions Assumptions = TradingAssumptionsDefaults.Create();

    // JST 月曜 09:05（UTC 00:05）。**固定値のみ**（テストが決定的である）。
    private static readonly DateTimeOffset Mon = new(2026, 8, 24, 0, 5, 0, TimeSpan.Zero);

    private static PeriodTradeFill Fill(
        string symbol, Market market, TradeSide side, int qty, decimal price, int minutes) =>
        new(symbol, market, side, side == TradeSide.Buy ? PositionEffect.Open : PositionEffect.Close,
            qty, price, Mon.AddMinutes(minutes));

    private static IReadOnlyList<FillPnlAttribution> Attributions(IReadOnlyList<PeriodTradeFill> fills) =>
        FillPnlAttributionBuilder.Build(fills, Assumptions, null);

    // 🔴 **持ち越し建玉が月内の週をまたぐ列**。ここが「週で切って集計器を呼び直す」実装との差が出る唯一の場所。
    // 月曜（W35）に建て、翌々週（W37）に決済する。
    private static IReadOnlyList<PeriodTradeFill> AcrossWeeks() =>
    [
        Fill("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 0),
        Fill("7203", Market.Japan, TradeSide.Buy, 100, 2_500m, 60),
        Fill("AAPL", Market.UnitedStates, TradeSide.Sell, 10, 1_200m, 60 * 24 * 14),
        Fill("7203", Market.Japan, TradeSide.Sell, 100, 2_400m, 60 * 24 * 15),
    ];

    // --- 内訳の和が §1 サマリと一致する ---

    [Fact]
    public void 週別の和が損益サマリと一致する()
    {
        var fills = AcrossWeeks();
        var summary = PnlAggregator.Aggregate(fills, Assumptions);
        var rows = PeriodBreakdownBuilder.ByWeek(Attributions(fills));

        // 週別の「実現損益」は税引前・費用込みであるため、和は（税引前の実現損益 − 費用合計）になる。
        rows.Sum(r => r.RealizedPnlAfterCost).Should().Be(summary.RealizedPnlGross - summary.TotalCost);
        rows.Sum(r => r.FillCount).Should().Be(summary.TradeCount);
        rows.Sum(r => r.RealizingCount).Should().Be(summary.RealizingTradeCount);
    }

    [Fact]
    public void 市場別の和が損益サマリと一致する()
    {
        var fills = AcrossWeeks();
        var summary = PnlAggregator.Aggregate(fills, Assumptions);
        var rows = PeriodBreakdownBuilder.ByMarket(Attributions(fills));

        rows.Sum(r => r.RealizedPnlGross).Should().Be(summary.RealizedPnlGross);
        rows.Sum(r => r.Cost).Should().Be(summary.TotalCost);
        rows.Sum(r => r.FillCount).Should().Be(summary.TradeCount);
    }

    [Fact]
    public void 方向別の和が損益サマリと一致する()
    {
        var fills = AcrossWeeks();
        var summary = PnlAggregator.Aggregate(fills, Assumptions);
        var rows = PeriodBreakdownBuilder.ByDirection(Attributions(fills));

        rows.Sum(r => r.RealizedPnlGross).Should().Be(summary.RealizedPnlGross);
        rows.Sum(r => r.Cost).Should().Be(summary.TotalCost);
        rows.Sum(r => r.FillCount).Should().Be(summary.TradeCount);
        rows.Sum(r => r.RealizingCount).Should().Be(summary.RealizingTradeCount);
        rows.Sum(r => r.WinningCount).Should().Be(summary.WinningTradeCount);
    }

    // 🔴 **採らなかった形が壊れることを実測で固定する**（IADR-0301 の同型テストの月報版）。
    // 週で切って集計器を呼び直すと、持ち越し建玉の平均取得単価がスライス内に無いため合計がずれる。
    [Fact]
    public void 週を切って集計器を呼び直すと持ち越し建玉の分だけ合計がずれる()
    {
        var fills = AcrossWeeks();
        var whole = PnlAggregator.Aggregate(fills, Assumptions);

        var sliced = fills
            .GroupBy(f => ReportPeriod.Label(
                ReportKind.Weekly, DateOnly.FromDateTime(f.ExecutedAt.ToOffset(ReportSchedule.JstOffset).DateTime)))
            .Sum(g => PnlAggregator.Aggregate([.. g], Assumptions).RealizedPnlGross);

        // 建てた週と決済した週が分かれるため、スライスの合計は実現損益を取りこぼす。
        sliced.Should().NotBe(whole.RealizedPnlGross);

        // 一方、帰属から数えた内訳は一致する（これが本実装の形である）。
        PeriodBreakdownBuilder.ByWeek(Attributions(fills)).Sum(r => r.RealizedPnlGross)
            .Should().Be(whole.RealizedPnlGross);
    }

    // --- 週別の行の出し方 ---

    [Fact]
    public void 約定のあった週だけがISO週ラベルの昇順で行になる()
    {
        var rows = PeriodBreakdownBuilder.ByWeek(Attributions(AcrossWeeks()));

        rows.Select(r => r.WeekLabel).Should().Equal("2026-W35", "2026-W37");
        // 約定が 1 件も無い W36 は行そのものが無い（「実現損益 0」ではない）。
        rows.Should().NotContain(r => r.WeekLabel == "2026-W36");
    }

    // 🔴 ISO 週は**年跨ぎで前年のラベルになる**（2027-01-01 は金曜で 2026-W53）。ラベルが年を含むため取り違えない。
    [Fact]
    public void 年跨ぎの週は前年のISO週ラベルになる()
    {
        var fills = new[]
        {
            new PeriodTradeFill("AAPL", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open,
                10, 1_000m, new DateTimeOffset(2027, 1, 1, 0, 5, 0, TimeSpan.Zero)),
        };

        PeriodBreakdownBuilder.ByWeek(Attributions(fills)).Single().WeekLabel.Should().Be("2026-W53");
    }

    [Fact]
    public void 決済が無い週の寄与最大は空であり損益0とは別である()
    {
        var fills = new[] { Fill("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 0) };

        var row = PeriodBreakdownBuilder.ByWeek(Attributions(fills)).Single();

        row.LargestContributor.Should().BeNull();
        row.RealizingCount.Should().Be(0);
        row.FillCount.Should().Be(1);
    }

    // --- 市場別 ---

    // 🔴 **約定が 1 件も無い市場も行を出す**（計画が行を固定している）。
    [Fact]
    public void 約定が無い市場も行を出し件数0で区別できるようにする()
    {
        var fills = new[] { Fill("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 0) };

        var rows = PeriodBreakdownBuilder.ByMarket(Attributions(fills));

        rows.Should().HaveCount(2);
        var japan = rows.Single(r => r.Market == Market.Japan);
        japan.FillCount.Should().Be(0);
        japan.Best.Should().BeNull();
        japan.Worst.Should().BeNull();
    }

    [Fact]
    public void 主要銘柄は決済のみを母集合にして上位と下位を選ぶ()
    {
        var fills = new[]
        {
            Fill("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 0),
            Fill("AAPL", Market.UnitedStates, TradeSide.Sell, 10, 1_200m, 60),   // +2,000
            Fill("TSLA", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 120),
            Fill("TSLA", Market.UnitedStates, TradeSide.Sell, 10, 900m, 180),    // -1,000
        };

        var row = PeriodBreakdownBuilder.ByMarket(Attributions(fills)).Single(r => r.Market == Market.UnitedStates);

        row.Best!.Value.Symbol.Should().Be("AAPL");
        row.Best!.Value.RealizedPnlGross.Should().Be(2_000m);
        row.Worst!.Value.Symbol.Should().Be("TSLA");
        row.Worst!.Value.RealizedPnlGross.Should().Be(-1_000m);
    }

    // --- 建玉の方向（帰属の既存フィールドだけから導く） ---

    [Fact]
    public void ロングの建てと決済はロング側に数える()
    {
        var fills = new[]
        {
            Fill("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 0),
            Fill("AAPL", Market.UnitedStates, TradeSide.Sell, 10, 1_200m, 60),
        };

        var rows = PeriodBreakdownBuilder.ByDirection(Attributions(fills));

        rows.Single(r => r.IsLong).FillCount.Should().Be(2);
        rows.Single(r => r.IsLong).RealizedPnlGross.Should().Be(2_000m);
        rows.Single(r => !r.IsLong).FillCount.Should().Be(0);
    }

    [Fact]
    public void ショートの建てと決済はショート側に数える()
    {
        var fills = new[]
        {
            Fill("AAPL", Market.UnitedStates, TradeSide.Sell, 10, 1_200m, 0),
            Fill("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 60),
        };

        var rows = PeriodBreakdownBuilder.ByDirection(Attributions(fills));

        rows.Single(r => !r.IsLong).FillCount.Should().Be(2);
        rows.Single(r => !r.IsLong).RealizedPnlGross.Should().Be(2_000m);
        rows.Single(r => r.IsLong).FillCount.Should().Be(0);
    }

    // 🔴 **反転（1 約定が「ロングの全決済＋ショートの新規建て」を兼ねる）を 2 行へ割らない。**
    // 割ると取引数の合計が §1 サマリと合わなくなる。
    [Fact]
    public void 反転の約定は決済した側に数え1約定を2行へ割らない()
    {
        var fills = new[]
        {
            Fill("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 0),      // ロング +10
            Fill("AAPL", Market.UnitedStates, TradeSide.Sell, 25, 1_200m, 60),    // 全決済＋ショート 15 の新規建て
        };

        var rows = PeriodBreakdownBuilder.ByDirection(Attributions(fills));

        rows.Sum(r => r.FillCount).Should().Be(2);
        // 決済（Realizing）かつ Sell なのでロング側。
        rows.Single(r => r.IsLong).FillCount.Should().Be(2);
        rows.Single(r => !r.IsLong).FillCount.Should().Be(0);
    }

    [Fact]
    public void 勝ち決済の件数は方向ごとに数える()
    {
        var fills = new[]
        {
            Fill("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 0),
            Fill("AAPL", Market.UnitedStates, TradeSide.Sell, 10, 1_200m, 60),   // ロングの勝ち決済
            Fill("TSLA", Market.UnitedStates, TradeSide.Sell, 10, 1_000m, 120),
            Fill("TSLA", Market.UnitedStates, TradeSide.Buy, 10, 1_200m, 180),   // ショートの負け決済
        };

        var rows = PeriodBreakdownBuilder.ByDirection(Attributions(fills));

        rows.Single(r => r.IsLong).WinningCount.Should().Be(1);
        rows.Single(r => r.IsLong).RealizingCount.Should().Be(1);
        rows.Single(r => !r.IsLong).WinningCount.Should().Be(0);
        rows.Single(r => !r.IsLong).RealizingCount.Should().Be(1);
    }
}
