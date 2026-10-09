using System.Net;
using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TradeDecisionService.Tests;

// 🔴 FR-02, FR-04, UC-01, #1286, IADR-0521 決定 1: 定時サイクルの判断対象は「監視銘柄 ∪ 保有中の銘柄」である。
// 監視銘柄はそのままの順で出口専用にせず、監視銘柄に無い保有銘柄だけを末尾に出口専用で足す。保有が不明なら監視銘柄だけ。
public class ScheduledJudgmentTargetsTests
{
    private static WatchedSymbol Us(string symbol) => new(symbol, Market.UnitedStates);

    // T-10-2480: PoC（2026-10-09）の形 —— 監視銘柄 8 件と、その外の保有 4 件。
    [Fact]
    public void T_10_2480_監視銘柄の外の保有銘柄を末尾に出口専用で足す()
    {
        WatchedSymbol[] watchlist = [Us("NVDA"), Us("META"), Us("TSLA"), Us("COIN"), Us("MARA"), Us("MSTR"), Us("SMCI"), Us("PLTR")];
        WatchedSymbol[] held = [Us("AAPL"), Us("MSFT"), Us("AMZN"), Us("GOOGL")];

        var targets = ScheduledJudgmentTargets.Build(watchlist, held);

        targets.Should().HaveCount(12);
        targets.Take(8).Should().Equal(watchlist.Select(w => (w, false)), "監視銘柄は順もそのまま・出口専用にしない");
        targets.Skip(8).Should().Equal(held.Select(h => (h, true)), "保有のみの銘柄は保有の応答の順で出口専用");
    }

    // T-10-2480（重なり）: 監視銘柄に在る保有銘柄は 1 回だけ、出口専用にせずに判断する（新規建ても従来どおり判断できる）。
    // 照合は前後の空白を除き大文字小文字を区別しない。市場が違えば別の銘柄。
    [Fact]
    public void T_10_2480_監視銘柄に在る保有銘柄は重ねず出口専用にしない()
    {
        WatchedSymbol[] watchlist = [Us("AAPL"), Us("NVDA")];
        WatchedSymbol[] held = [Us(" aapl "), new("NVDA", Market.Japan), Us("MSFT"), Us("MSFT")];

        var targets = ScheduledJudgmentTargets.Build(watchlist, held);

        targets.Should().Equal(
            (Us("AAPL"), false), (Us("NVDA"), false), (new WatchedSymbol("NVDA", Market.Japan), true), (Us("MSFT"), true));
    }

    // T-10-2480（不明・空）: 保有が不明（null）なら監視銘柄だけ（従来の巡回）。空なら監視銘柄だけ。監視銘柄が空でも保有は判断する。
    [Fact]
    public void T_10_2480_保有が不明なら監視銘柄だけを判断する()
    {
        ScheduledJudgmentTargets.Build([Us("AAPL")], null).Should().Equal((Us("AAPL"), false));
        ScheduledJudgmentTargets.Build([Us("AAPL")], []).Should().Equal((Us("AAPL"), false));
        ScheduledJudgmentTargets.Build([], [Us("MSFT")]).Should().Equal((Us("MSFT"), true));
    }

    // T-10-2481: 保有銘柄は open-positions の全行から (銘柄, 市場) ごとに符号付きで合計し、0 でないものだけを返す。
    [Fact]
    public async Task T_10_2481_保有銘柄は銘柄と市場ごとに合計して0でないものを返す()
    {
        const string body = """
            [
              {"symbol":"AAPL","market":1,"side":0,"quantity":10,"entryPrice":200,"stopLossPrice":190},
              {"symbol":"MSFT","market":1,"side":0,"quantity":5,"entryPrice":400,"stopLossPrice":380},
              {"symbol":"MSFT","market":1,"side":1,"quantity":5,"entryPrice":400,"stopLossPrice":420},
              {"symbol":"TSLA","market":1,"side":1,"quantity":3,"entryPrice":250,"stopLossPrice":260}
            ]
            """;

        var held = await Provider(HttpStatusCode.OK, body).GetHeldSymbolsAsync(TestContext.Current.CancellationToken);

        held.Should().Equal(Us("AAPL"), Us("TSLA"));
    }

    // T-10-2481（不明）: 照会の失敗・解釈できない行は null（不明）。空配列は空（保有なし）。不明を空へ倒さない。
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "[]")]
    [InlineData(HttpStatusCode.OK, "null")]
    [InlineData(HttpStatusCode.OK, """[{"symbol":"AAPL","market":1,"side":0,"quantity":10}, {"market":1,"side":0,"quantity":5}]""")]
    [InlineData(HttpStatusCode.OK, """[{"symbol":"AAPL","market":1,"side":0,"quantity":0}]""")]
    [InlineData(HttpStatusCode.OK, """[{"symbol":"AAPL","market":1,"quantity":10}]""")]
    public async Task T_10_2481_照会の失敗や解釈できない応答は不明_否定形(HttpStatusCode status, string body)
    {
        var held = await Provider(status, body).GetHeldSymbolsAsync(TestContext.Current.CancellationToken);

        held.Should().BeNull("不明を「保有なし」へ倒すと、保有のみの銘柄が黙って判断対象から落ちる");
    }

    [Fact]
    public async Task T_10_2481_空配列は保有なしの空()
    {
        var held = await Provider(HttpStatusCode.OK, "[]").GetHeldSymbolsAsync(TestContext.Current.CancellationToken);

        held.Should().NotBeNull().And.BeEmpty();
    }

    // T-10-2481（未結線）: NoOp は常に不明（定時サイクルは監視銘柄だけを判断する＝従来どおり）。
    [Fact]
    public async Task T_10_2481_未結線は不明()
    {
        var held = await new NoOpHeldPositionProvider().GetHeldSymbolsAsync(TestContext.Current.CancellationToken);

        held.Should().BeNull();
    }

    private static HttpHeldPositionProvider Provider(HttpStatusCode status, string body) =>
        new(new HttpClient(new StubHandler(status, body)) { BaseAddress = new Uri("http://risk") },
            NullLogger<HttpHeldPositionProvider>.Instance);

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
