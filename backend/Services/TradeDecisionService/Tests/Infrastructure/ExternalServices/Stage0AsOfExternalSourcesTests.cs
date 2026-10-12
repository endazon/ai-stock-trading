extern alias ReportWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ReportWorker::ReportService.Features.Reports;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
using TradeDecisionService.Infrastructure.ExternalServices;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-01, FR-04, FR-08, FR-15, ADR-0033 決定2, #1308, IADR-0528 決定2・3・4・7: Stage 0 の as-of 入力の基底の外部の口。
//
// 見るのは 3 点である。
//   1. 🔴 判断時点で切った要求を出す（日足の期間・方針の時刻・企業ニュースの日付・ALFRED の前日の版）。
//   2. 🔴 「当時無かった」（休場・確定日報なし・その期間の記事なし）と「取れなかった」（失敗）を分ける。
//      価格と方針の失敗は撃ち直してから例外（記録を止める）。参考情報の源の失敗は null（(b) を再構成不可にする）。
//   3. 市場の源（FRED）は判断日ごとに 1 回だけ取り、銘柄ごとに撃ち直さない（#1323 の AI レビュー 🟢）。
public class Stage0AsOfExternalSourcesTests
{
    private static readonly DateOnly AsOf = new(2026, 9, 15);
    private static readonly DateTimeOffset At = new(2026, 9, 15, 20, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    // 報告書の Program.cs（ConfigureHttpJsonOptions に JsonStringEnumConverter を足す）と同じ設定。
    private static readonly JsonSerializerOptions ReportWire =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // ------------------------------------------------------------------------------------------------
    // 価格（発注執行の日足）
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task 価格は判断日の足の終値と前日の終値を返し期間は判断日で切る()
    {
        var bars = new FakeBars(_ => [Bar(AsOf.AddDays(-1), 100m), Bar(AsOf, 101m), Bar(AsOf.AddDays(-4), 95m)]);

        var reading = await Prices(bars).GetAsync("AAPL", Market.UnitedStates, AsOf, TestContext.Current.CancellationToken);

        reading.Close.Should().Be(new DatedPrice(AsOf, 101m));
        reading.PreviousClose.Should().Be(new DatedPrice(AsOf.AddDays(-1), 100m));
        bars.Requests.Should().Equal(("AAPL", AsOf.AddDays(-10), AsOf));
    }

    [Fact]
    public async Task 判断日の足が無ければ参照価格は無い_休場()
    {
        var bars = new FakeBars(_ => [Bar(AsOf.AddDays(-1), 100m)]);

        var reading = await Prices(bars).GetAsync("AAPL", Market.UnitedStates, AsOf, TestContext.Current.CancellationToken);

        reading.Close.Should().BeNull();
        bars.Requests.Should().HaveCount(1, "取れた（足が無いだけ）なので撃ち直さない");
    }

    [Fact]
    public async Task 日足が取れなければ撃ち直してから記録を止める()
    {
        var bars = new FakeBars(_ => null);

        var act = () => Prices(bars).GetAsync("AAPL", Market.UnitedStates, AsOf, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<AsOfInputUnavailableException>();
        bars.Requests.Should().HaveCount(3);
    }

    // 🔴 否定形: 今日の取引日以降の足は未確定である。使わずに止める。
    [Fact]
    public async Task 判断日が今日の取引日以降なら足を取らずに止める()
    {
        var bars = new FakeBars(_ => [Bar(AsOf, 1m)]);
        var source = new DailyBarsAsOfPriceSource(bars, new FixedTime(new DateTimeOffset(2026, 9, 15, 18, 0, 0, TimeSpan.Zero)),
            NullLogger<DailyBarsAsOfPriceSource>.Instance, TimeSpan.Zero);

        var act = () => source.GetAsync("AAPL", Market.UnitedStates, AsOf, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<AsOfInputUnavailableException>();
        bars.Requests.Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------------------------
    // 方針（報告書サービス）
    // ------------------------------------------------------------------------------------------------

    // 契約（IADR-0420）: 送り手の本物の型 ConfirmedDailyPolicyAsOf を送り手の JSON 設定で直列化した応答を読む。
    [Fact]
    public async Task 方針は送り手の本物の型を直列化した応答から読み_時刻はUTCで渡す()
    {
        var body = JsonSerializer.Serialize(
            new ConfirmedDailyPolicyAsOf(AsOf.AddDays(-1), "前日に確定した方針", 3, At.AddHours(-20)), ReportWire);
        var handler = new Scripted(_ => Json(HttpStatusCode.OK, body));

        var policy = await Policies(handler).GetAsync(new DateTimeOffset(2026, 9, 15, 16, 0, 0, TimeSpan.FromHours(-4)), TestContext.Current.CancellationToken);

        (policy!.Date, policy.Summary).Should().Be((AsOf.AddDays(-1), "前日に確定した方針"));
        handler.Requests.Should().Equal("/reports/daily-policy/as-of?at=2026-09-15T20%3A00%3A00Z");
    }

    [Fact]
    public async Task 確定日報が無ければ方針は無い_404()
    {
        var handler = new Scripted(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        (await Policies(handler).GetAsync(At, TestContext.Current.CancellationToken)).Should().BeNull();
        handler.Requests.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("500")]
    [InlineData("future")]
    public async Task 方針が取れない_判断時点より後に確定した方針は撃ち直してから記録を止める(string mode)
    {
        var handler = new Scripted(_ => mode == "500"
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : Json(HttpStatusCode.OK, JsonSerializer.Serialize(new ConfirmedDailyPolicyAsOf(AsOf, "未来に確定", 1, At.AddSeconds(1)), ReportWire)));

        var act = () => Policies(handler).GetAsync(At, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<AsOfInputUnavailableException>();
        handler.Requests.Should().HaveCount(3);
    }

    // ------------------------------------------------------------------------------------------------
    // 参考情報の 3 源
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task 企業ニュースは判断時点の日付で区切って引き_収集と同じ形の文書にする()
    {
        var handler = new Scripted(_ => Json(HttpStatusCode.OK,
            """[{"headline":"見出し","summary":"要約","url":"https://example.com/a","datetime":1789502400},{"headline":"","summary":"x","datetime":1}]"""));
        var feed = Finnhub(handler);

        var docs = await feed.FetchAsync("AAPL", Market.UnitedStates, At.AddHours(-168), At, TestContext.Current.CancellationToken);

        handler.Requests.Should().ContainSingle().Which.Should()
            .Contain("symbol=AAPL&from=2026-09-08&to=2026-09-15&token=key");
        var doc = docs.Should().ContainSingle().Subject;
        (doc.Title, doc.Text, doc.SourceUri).Should().Be(("見出し", "要約", "https://example.com/a"));
        doc.Tags.Should().Equal("News", "finnhub-news");
        doc.PublishedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1789502400));
    }

    [Fact]
    public async Task 企業ニュースは429なら1回だけ撃ち直し_失敗はnull_米国株以外は無い()
    {
        var calls = 0;
        var flaky = new Scripted(_ => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Json(HttpStatusCode.OK, "[]"));
        (await Finnhub(flaky).FetchAsync("AAPL", Market.UnitedStates, At.AddDays(-7), At, TestContext.Current.CancellationToken))
            .Should().BeEmpty();
        flaky.Requests.Should().HaveCount(2);

        var failing = new Scripted(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        (await Finnhub(failing).FetchAsync("AAPL", Market.UnitedStates, At.AddDays(-7), At, TestContext.Current.CancellationToken))
            .Should().BeNull("取れなかった（(b) を再構成不可にする）");

        var unused = new Scripted(_ => throw new InvalidOperationException("呼ばない"));
        (await Finnhub(unused).FetchAsync("7203", Market.Japan, At.AddDays(-7), At, TestContext.Current.CancellationToken))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task 開示はCIKごとに1回だけ引き_銘柄で絞り_受理時刻を発行時刻にする()
    {
        const string submissions = """
            {"name":"Apple Inc.","tickers":["AAPL"],"filings":{"recent":{
              "accessionNumber":["0000320193-26-000001","0000320193-26-000002"],
              "filingDate":["2026-09-14","2026-09-01"],
              "reportDate":["",""],
              "acceptanceDateTime":["2026-09-14T16:30:00.000Z",""],
              "form":["8-K","4"],
              "primaryDocument":["a.htm","b.xml"],
              "primaryDocDescription":["8-K","4"]}}}
            """;
        var handler = new Scripted(_ => Json(HttpStatusCode.OK, submissions));
        var feed = new SecEdgarAsOfFilingFeed(new HttpClient(handler), "AST test@example.com", ["320193"], NullLogger<SecEdgarAsOfFilingFeed>.Instance);

        var aapl = await feed.FetchAsync("AAPL", Market.UnitedStates, At.AddDays(-7), At, TestContext.Current.CancellationToken);
        var msft = await feed.FetchAsync("MSFT", Market.UnitedStates, At.AddDays(-7), At, TestContext.Current.CancellationToken);

        handler.Requests.Should().Equal("/submissions/CIK0000320193.json");
        msft.Should().BeEmpty("構成した CIK の銘柄ではない＝本番にも無い");
        aapl!.Select(d => (d.Title, d.PublishedAt)).Should().Equal(
            ("Apple Inc. 8-K", new DateTimeOffset(2026, 9, 14, 16, 30, 0, TimeSpan.Zero)),
            ("Apple Inc. 4", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)));
        aapl![0].Tags.Should().Equal("Disclosure", "sec-edgar");
    }

    [Fact]
    public async Task 開示の取得に失敗すればnullで_失敗は覚えない()
    {
        var calls = 0;
        var handler = new Scripted(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Json(HttpStatusCode.OK, """{"name":"Apple Inc.","tickers":["AAPL"],"filings":{"recent":{"accessionNumber":[]}}}"""));
        var feed = new SecEdgarAsOfFilingFeed(new HttpClient(handler), "AST test@example.com", ["320193"], NullLogger<SecEdgarAsOfFilingFeed>.Instance);

        (await feed.FetchAsync("AAPL", Market.UnitedStates, At.AddDays(-7), At, TestContext.Current.CancellationToken)).Should().BeNull();
        (await feed.FetchAsync("AAPL", Market.UnitedStates, At.AddDays(-7), At, TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task マクロ指標はALFREDの前日の版で引き_判断日ごとに1回だけ取る()
    {
        var handler = new Scripted(_ => Json(HttpStatusCode.OK,
            """{"observations":[{"date":"2026-09-10","value":"146.5"},{"date":"2026-09-11","value":"."}]}"""));
        var feed = new FredAsOfObservationFeed(new HttpClient(handler), "key", ["DEXJPUS"], NullLogger<FredAsOfObservationFeed>.Instance);

        var first = await feed.FetchAsync("AAPL", Market.UnitedStates, At.AddHours(-168), At, TestContext.Current.CancellationToken);
        var second = await feed.FetchAsync("MSFT", Market.UnitedStates, At.AddHours(-168), At, TestContext.Current.CancellationToken);

        handler.Requests.Should().ContainSingle("同じ判断日の 2 銘柄目は撃ち直さない").Which.Should().Contain(
            "series_id=DEXJPUS&realtime_start=2026-09-14&realtime_end=2026-09-14&observation_start=2026-09-07&observation_end=2026-09-15");
        var doc = first.Should().ContainSingle("数値でない観測（.）は採らない").Subject;
        (doc.Title, doc.Text).Should().Be(("FRED DEXJPUS", "seriesId=DEXJPUS;value=146.5;date=2026-09-10"));
        doc.PublishedAt.Should().Be(new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));
        doc.Tags.Should().Equal("MacroIndicator", "fred");
        second.Should().BeEquivalentTo(first);
    }

    // ------------------------------------------------------------------------------------------------
    // 組み立て（構成の無い源は入れない）
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, false, false, new string[0])]
    [InlineData(true, false, false, new[] { "finnhub-news" })]
    [InlineData(true, true, true, new[] { "finnhub-news", "sec-edgar", "fred" })]
    public void 構成の無い源は組み立てに入れない(bool finnhub, bool edgar, bool fred, string[] expected)
    {
        var settings = new Dictionary<string, string?>();
        if (finnhub)
            settings["Stage0AsOf:Finnhub:ApiKey"] = "key";
        if (edgar)
        {
            settings["Stage0AsOf:SecEdgar:UserAgent"] = "AST test@example.com";
            settings["Stage0AsOf:SecEdgar:Ciks:0"] = "0000320193";
        }

        if (fred)
        {
            settings["Stage0AsOf:Fred:ApiKey"] = "key";
            settings["Stage0AsOf:Fred:SeriesIds:0"] = "DEXJPUS";
        }

        // UserAgent だけ・CIK だけ・鍵だけ・系列だけは構成とみなさない。
        settings["Stage0AsOf:Fred:SeriesIds:9"] = "DGS10";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var feeds = Stage0AsOfComposition.Feeds(configuration, new SimpleFactory(), TimeProvider.System, NullLoggerFactory.Instance);

        feeds.Select(f => f.SourceName).Should().Equal(expected);
    }

    // ------------------------------------------------------------------------------------------------
    // 補助
    // ------------------------------------------------------------------------------------------------

    private static DailyBarsAsOfPriceSource Prices(IDailyBarsSource bars) =>
        new(bars, new FixedTime(Now), NullLogger<DailyBarsAsOfPriceSource>.Instance, TimeSpan.Zero);

    private static HttpAsOfDailyPolicySource Policies(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://report-service:8080") }, new FixedTime(Now),
            NullLogger<HttpAsOfDailyPolicySource>.Instance, TimeSpan.Zero);

    private static FinnhubAsOfCompanyNewsFeed Finnhub(HttpMessageHandler handler) =>
        new(new HttpClient(handler), "key", new NoWait(), new FixedTime(Now), NullLogger<FinnhubAsOfCompanyNewsFeed>.Instance, TimeSpan.Zero);

    private static DailyBar Bar(DateOnly date, decimal close) => new(date, close, close, close, close, 1_000);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeBars(Func<DateOnly, IReadOnlyList<DailyBar>?> respond) : IDailyBarsSource
    {
        public List<(string, DateOnly, DateOnly)> Requests { get; } = [];

        public Task<IReadOnlyList<DailyBar>?> FetchAsync(
            string symbol, Market market, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
        {
            Requests.Add((symbol, from, to));
            return Task.FromResult(respond(to));
        }
    }

    private sealed class Scripted(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class NoWait : IRateLimiter
    {
        public Task WaitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SimpleFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
