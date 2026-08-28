using ReportService.Application.Ports;
using ReportService.Application.Services;
using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Application.Tests;

// #81, FR-16, IADR-0025/0066: 評価損益の現在値供給（要求未指定時のみ市場データ源から補完する）を検証する。
public class ReportDraftCurrentPriceTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);

    private sealed class FakeDrafter : IReportNarrativeDrafter
    {
        public Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult("散文");
    }

    // 指定した (銘柄, 市場) にだけ現在値を返すスタブ。引かれた組を記録する。
    private sealed class StubMarketData(Dictionary<(string Symbol, Market Market), decimal> prices) : IMarketDataSource
    {
        public List<(string Symbol, Market Market)> Requested { get; } = [];

        public Task<Quote?> GetLatestQuoteAsync(string symbol, Market market, CancellationToken cancellationToken = default)
        {
            Requested.Add((symbol, market));
            return Task.FromResult(prices.TryGetValue((symbol, market), out var price)
                ? new Quote(symbol, market, price, At)
                : null);
        }
    }

    private static PeriodTradeFill Fill(string symbol, Market market, TradeSide side, int qty, decimal price, int minute) =>
        new(symbol, market, side, side == TradeSide.Buy ? PositionEffect.Open : PositionEffect.Close,
            qty, price, At.AddMinutes(minute));

    private static DraftRequest Request(
        IReadOnlyList<PeriodTradeFill> fills,
        IReadOnlyDictionary<string, decimal>? currentPrices = null) =>
        new(ReportKind.Daily, "daily-2026-07-10", new DateOnly(2026, 7, 10), ["US"], 1, null, "方針", fills, currentPrices);

    [Fact]
    public async Task 要求に現在値が無ければ市場データ源から補完して評価損益を算出する()
    {
        // 建玉 10 @1,000 が 1,100 → 評価損益 +1,000。
        var fills = new[] { Fill("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 0) };
        var market = new StubMarketData(new() { [("AAPL", Market.UnitedStates)] = 1_100m });
        var sut = new ReportDraftService(new FakeDrafter(), market);

        var draft = await sut.BuildDraftAsync(Request(fills));

        draft.Pnl.UnrealizedPnl.Should().Be(1_000m);
    }

    [Fact]
    public async Task 要求が現在値を指定していれば市場データ源を引かない()
    {
        var fills = new[] { Fill("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 0) };
        var market = new StubMarketData(new() { [("AAPL", Market.UnitedStates)] = 9_999m });
        var sut = new ReportDraftService(new FakeDrafter(), market);

        // 要求指定（1,050 → +500）を優先し、市場データ源で上書きしない（既存 API 互換）。
        var draft = await sut.BuildDraftAsync(Request(fills, new Dictionary<string, decimal> { ["AAPL"] = 1_050m }));

        draft.Pnl.UnrealizedPnl.Should().Be(500m);
        market.Requested.Should().BeEmpty();
    }

    [Fact]
    public async Task 市場データ源が未注入なら評価損益は0のまま()
    {
        // 既定（実市況未接続）＝現行挙動。
        var fills = new[] { Fill("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 0) };
        var sut = new ReportDraftService(new FakeDrafter());

        (await sut.BuildDraftAsync(Request(fills))).Pnl.UnrealizedPnl.Should().Be(0m);
    }

    [Fact]
    public async Task 現在値が取得できない銘柄は評価損益0として扱う()
    {
        var fills = new[] { Fill("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 0) };
        var sut = new ReportDraftService(new FakeDrafter(), new StubMarketData([]));

        (await sut.BuildDraftAsync(Request(fills))).Pnl.UnrealizedPnl.Should().Be(0m);
    }

    [Fact]
    public async Task 同一銘柄コードが複数市場にあれば市場を判別できないため現在値を落とす()
    {
        // PnlAggregator の現在値辞書は銘柄コードのみがキー（IADR-0025）。誤った市場の価格で評価するより 0 に倒す。
        var fills = new[]
        {
            Fill("7203", Market.Japan, TradeSide.Buy, 10, 1_000m, 0),
            Fill("7203", Market.UnitedStates, TradeSide.Buy, 10, 2_000m, 1),
        };
        var market = new StubMarketData(new()
        {
            [("7203", Market.Japan)] = 1_100m,
            [("7203", Market.UnitedStates)] = 2_200m,
        });
        var sut = new ReportDraftService(new FakeDrafter(), market);

        (await sut.BuildDraftAsync(Request(fills))).Pnl.UnrealizedPnl.Should().Be(0m);
    }

    [Fact]
    public async Task 曖昧な銘柄があっても他の銘柄の現在値は供給する()
    {
        var fills = new[]
        {
            Fill("7203", Market.Japan, TradeSide.Buy, 10, 1_000m, 0),
            Fill("7203", Market.UnitedStates, TradeSide.Buy, 10, 2_000m, 1),
            Fill("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 2),
        };
        var market = new StubMarketData(new()
        {
            [("7203", Market.Japan)] = 1_100m,
            [("7203", Market.UnitedStates)] = 2_200m,
            [("AAPL", Market.UnitedStates)] = 1_100m,
        });
        var sut = new ReportDraftService(new FakeDrafter(), market);

        // 7203 は曖昧で落とし、AAPL のみ +1,000。
        (await sut.BuildDraftAsync(Request(fills))).Pnl.UnrealizedPnl.Should().Be(1_000m);
    }

    [Fact]
    public async Task 全決済済みの銘柄には現在値を引かない()
    {
        // 建玉が残らない銘柄の現在値は評価に不要（無駄な市況取得をしない）。
        var fills = new[]
        {
            Fill("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 0),
            Fill("AAPL", Market.UnitedStates, TradeSide.Sell, 10, 1_200m, 1),
        };
        var market = new StubMarketData([]);
        var sut = new ReportDraftService(new FakeDrafter(), market);

        await sut.BuildDraftAsync(Request(fills));

        market.Requested.Should().BeEmpty();
    }
}
