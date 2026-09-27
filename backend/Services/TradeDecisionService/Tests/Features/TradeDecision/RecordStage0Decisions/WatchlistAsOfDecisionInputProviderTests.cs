extern alias RiskManagementWorker;

using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
using Xunit;

namespace TradeDecisionService.Tests.Features.TradeDecision.RecordStage0Decisions;

// T-10-1630, FR-04, FR-15, ADR-0044 決定 3, ADR-0046 決定 1, #1049, IADR-0442 決定 3・4: as-of 入力の供給を包み、当時の監視銘柄を埋めるデコレータと、
// 判断時点を照会の時刻へ換える規則。
public class WatchlistAsOfDecisionInputProviderTests
{
    private static readonly DateOnly Day1 = new(2026, 6, 1);
    private static readonly DateOnly Day2 = new(2026, 6, 2);

    private static SizingContext Sizing() =>
        new(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

    private static AsOfDecisionInput Input(DateOnly asOf, IReadOnlyList<WatchedSymbol>? watchlist = null) =>
        new(asOf, new DailyPolicy(asOf, "方針"), Sizing(), new DatedPrice(asOf, 100m), watchlist: watchlist);

    // 🔴 境界値: 判断時点は AsOf の UTC の日の終わり（含む）。翌日 0 時ちょうどは含まない。
    [Fact]
    public void 判断時点はAsOfのUTCの日の終わりで翌日0時は含まない()
    {
        var at = AsOfWatchlistInstant.EndOfUtcDay(Day2);

        at.Should().Be(new DateTimeOffset(2026, 6, 2, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_999));
        at.Offset.Should().Be(TimeSpan.Zero);
        (at + TimeSpan.FromTicks(1)).Should().Be(new DateTimeOffset(2026, 6, 3, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task 再構成できた一覧を埋め同じ判断時点は1回だけ照会する()
    {
        var source = new CountingSource(AsOfWatchlist.Reconstructed([new("META", Market.UnitedStates)]));
        var provider = new WatchlistAsOfDecisionInputProvider(new StubInner(asOf => Input(asOf)), source);

        var a = await provider.GetAsync("AAPL", Market.UnitedStates, Day1);
        var b = await provider.GetAsync("MSFT", Market.UnitedStates, Day1);
        var c = await provider.GetAsync("AAPL", Market.UnitedStates, Day2);

        a!.Watchlist.Should().Equal(new WatchedSymbol("META", Market.UnitedStates));
        b!.Watchlist.Should().Equal(new WatchedSymbol("META", Market.UnitedStates));
        c!.Watchlist.Should().Equal(new WatchedSymbol("META", Market.UnitedStates));
        source.Queried.Should().Equal(AsOfWatchlistInstant.EndOfUtcDay(Day1), AsOfWatchlistInstant.EndOfUtcDay(Day2));
        Stage0AsOfInputs.IsExcluded(a.AsOfInputs).Should().BeFalse();
    }

    // 🔴 否定形: 再構成できなければ一覧は null のまま（空の一覧にしない）で、理由を (e) の申告へ載せ、判断は合否から外れる。
    [Fact]
    public async Task 再構成できなければ一覧を埋めず理由を申告へ載せる()
    {
        var provider = new WatchlistAsOfDecisionInputProvider(
            new StubInner(asOf => Input(asOf)),
            new CountingSource(AsOfWatchlist.NotReconstructable("SeededAt が記録されていません。")));

        var input = await provider.GetAsync("AAPL", Market.UnitedStates, Day1);

        input!.Watchlist.Should().BeNull();
        input.NotReconstructableKinds.Should().Equal(Stage0AsOfInputKind.Watchlist);
        input.AsOfInputs.Single(s => s.Kind == Stage0AsOfInputKind.Watchlist).Reason
            .Should().Contain("SeededAt が記録されていません。");
    }

    [Fact]
    public async Task 内側が一覧を渡していれば上書きせず照会しない()
    {
        var source = new CountingSource(AsOfWatchlist.Reconstructed([new("META", Market.UnitedStates)]));
        var supplied = Input(Day1, watchlist: [new("NVDA", Market.UnitedStates)]);
        var provider = new WatchlistAsOfDecisionInputProvider(new StubInner(_ => supplied), source);

        var input = await provider.GetAsync("AAPL", Market.UnitedStates, Day1);

        input.Should().BeSameAs(supplied);
        source.Queried.Should().BeEmpty();
    }

    [Fact]
    public async Task 内側が入力なしなら照会せず入力なしを返す()
    {
        var source = new CountingSource(AsOfWatchlist.Reconstructed([]));
        var provider = new WatchlistAsOfDecisionInputProvider(new StubInner(_ => null), source);

        (await provider.GetAsync("AAPL", Market.UnitedStates, Day1)).Should().BeNull();
        source.Queried.Should().BeEmpty();
    }

    // 🔴 本番の既定: as-of の他の入力の実供給は無いので、デコレータ越しでも記録は作られない（LLM も照会も走らない）。
    [Fact]
    public async Task 既定の供給を包んでも入力なしのままで照会しない()
    {
        var source = new CountingSource(AsOfWatchlist.Reconstructed([]));
        var provider = new WatchlistAsOfDecisionInputProvider(new NoAsOfDecisionInputProvider(), source);

        (await provider.GetAsync("AAPL", Market.UnitedStates, Day1)).Should().BeNull();
        source.Queried.Should().BeEmpty();
    }

    // WithWatchlist は監視銘柄だけを差し替え、他の入力（as-of の切り方・供給側の申告）は同じ規律で組み直す。
    [Fact]
    public void 監視銘柄の差し替えは他の入力と申告を変えない()
    {
        var original = new AsOfDecisionInput(
            Day1, new DailyPolicy(Day1, "方針"), Sizing(), new DatedPrice(Day1, 123m),
            [
                new RetrievedContext("当日", "本文", "https://example.test/1", 0.9d, ["news"], new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)),
                new RetrievedContext("翌日", "本文", "https://example.test/2", 0.9d, ["news"], new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero)),
            ],
            rateToBase: 150m,
            notReconstructable: [Stage0AsOfInputKind.FxRateToBase]);

        var replaced = original.WithWatchlist([new("META", Market.UnitedStates)], unavailableReason: null);

        replaced.Watchlist.Should().Equal(new WatchedSymbol("META", Market.UnitedStates));
        replaced.ReferencePrice.Should().Be(123m);
        replaced.RateToBase.Should().Be(150m);
        replaced.References.Should().ContainSingle().Which.Title.Should().Be("当日");
        replaced.DroppedFutureReferenceCount.Should().Be(1);
        replaced.NotReconstructableKinds.Should().Equal(Stage0AsOfInputKind.FxRateToBase);
        original.Watchlist.Should().BeNull("元の入力は変わらない");
    }

    private sealed class StubInner(Func<DateOnly, AsOfDecisionInput?> make) : IAsOfDecisionInputProvider
    {
        public Task<AsOfDecisionInput?> GetAsync(
            string symbol, Market market, DateOnly asOf, CancellationToken cancellationToken = default) =>
            Task.FromResult(make(asOf));
    }

    private sealed class CountingSource(AsOfWatchlist result) : IAsOfWatchlistSource
    {
        public List<DateTimeOffset> Queried { get; } = [];

        public Task<AsOfWatchlist> GetWatchlistAtAsync(DateTimeOffset at, CancellationToken cancellationToken = default)
        {
            Queried.Add(at);
            return Task.FromResult(result);
        }
    }
}
