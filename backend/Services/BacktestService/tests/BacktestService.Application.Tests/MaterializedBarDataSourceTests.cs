using AiStockTrading.Backtest.Application;
using AiStockTrading.Backtest.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Backtest.Application.Tests;

// FR-15, #208, IADR-0105: 実データ源から取得したバーのスナップショット（本番経路の IBarDataSource）。
// 取得は 1 回・評価は純粋。正規化（重複排除・安定ソート）はここを単一情報源にする。
public class MaterializedBarDataSourceTests
{
    private static PriceBar Bar(string symbol, int day, decimal close) =>
        new(symbol, Market.UnitedStates, new DateOnly(2024, 1, day), close, close, close, close, 100);

    [Fact]
    public void 期間内のバーだけを返す()
    {
        var source = MaterializedBarDataSource.FromBars([Bar("AAA", 1, 10m), Bar("AAA", 5, 12m)]);

        source.GetBars(new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 3))
            .Should().ContainSingle().Which.Date.Should().Be(new DateOnly(2024, 1, 1));
    }

    [Fact]
    public void 同一銘柄同一日の重複を排除する_後勝ち()
    {
        // IBarDataSource の契約: 同一 (Symbol, Market, Date) は 1 本だけ返す（正規化はアダプタ側の責務）。
        var source = MaterializedBarDataSource.FromBars([Bar("AAA", 1, 10m), Bar("AAA", 1, 11m)]);

        var bars = source.GetBars(new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        bars.Should().ContainSingle();
        bars[0].Close.Should().Be(11m);
    }

    [Fact]
    public void 日付昇順_同日は銘柄順で安定ソートする()
    {
        var source = MaterializedBarDataSource.FromBars(
            [Bar("BBB", 2, 10m), Bar("AAA", 2, 10m), Bar("CCC", 1, 10m)]);

        var bars = source.GetBars(new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        bars.Select(b => (b.Date.Day, b.Symbol)).Should().Equal((1, "CCC"), (2, "AAA"), (2, "BBB"));
    }

    [Fact]
    public async Task ユニバースから取得してスナップショットを作る()
    {
        // 取得対象は PIT ユニバースから導出する（上場廃止銘柄を落とさない＝生存者バイアス排除をデータ取得段階から）。
        var universe = new SecurityUniverse(
        [
            new UniverseMembership("AAA", Market.UnitedStates, new DateOnly(2024, 1, 1), null),
            new UniverseMembership("BBB", Market.UnitedStates, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 3)),
        ]);
        var stub = new StubHistoricalBarSource([Bar("AAA", 1, 10m), Bar("BBB", 1, 20m)]);

        var source = await MaterializedBarDataSource.LoadAsync(
            stub, universe, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        stub.RequestedSymbols.Should().BeEquivalentTo([("AAA", Market.UnitedStates), ("BBB", Market.UnitedStates)]);
        source.GetBars(new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31)).Should().HaveCount(2);
    }

    [Fact]
    public async Task 欠測は取得結果として保持する()
    {
        // 無音破棄しない（BacktestRun.UnfilledOrderCount と同じ方針）。欠測のまま合格させないための材料。
        var universe = new SecurityUniverse([new UniverseMembership("AAA", Market.UnitedStates, new DateOnly(2024, 1, 1), null)]);
        var stub = new StubHistoricalBarSource([], [new HistoricalBarGap("AAA", Market.UnitedStates, "データなし")]);

        var source = await MaterializedBarDataSource.LoadAsync(
            stub, universe, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        source.Gaps.Should().ContainSingle().Which.Symbol.Should().Be("AAA");
        source.GetBars(new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31)).Should().BeEmpty();
    }

    [Fact]
    public async Task ユニバースが空なら取得しない()
    {
        var stub = new StubHistoricalBarSource([]);

        var source = await MaterializedBarDataSource.LoadAsync(
            stub, new SecurityUniverse([]), new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        stub.Calls.Should().Be(0);
        source.GetBars(new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31)).Should().BeEmpty();
    }

    private sealed class StubHistoricalBarSource(
        IReadOnlyList<PriceBar> bars, IReadOnlyList<HistoricalBarGap>? gaps = null) : IHistoricalBarSource
    {
        public List<(string Symbol, Market Market)> RequestedSymbols { get; } = [];
        public int Calls { get; private set; }

        public Task<HistoricalBarLoad> LoadBarsAsync(
            IReadOnlyList<(string Symbol, Market Market)> symbols,
            DateOnly from,
            DateOnly to,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            RequestedSymbols.AddRange(symbols);
            return Task.FromResult(new HistoricalBarLoad(bars, gaps ?? []));
        }
    }
}
