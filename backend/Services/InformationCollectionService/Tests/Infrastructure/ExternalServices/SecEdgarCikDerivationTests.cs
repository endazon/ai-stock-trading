using System.Net;
using System.Text;
using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InformationCollectionService.Tests;

// 🔴 FR-01, #1363, IADR-0547 決定 2・3: SEC EDGAR の CIK を固定値ではなく、収集の対象の銘柄（保有が先、続いて監視銘柄）から
// SEC の ticker→CIK の対応表（company_tickers.json）で導く。構成の固定 CIK は常に足す分として併用する。
public class SecEdgarCikDerivationTests
{
    private const string UserAgent = "AiStockTrading/1.0 (owner@example.com)";

    // company_tickers.json の形（必要な項目のみ）。
    private const string CompanyTickers = """
        {
          "0": {"cik_str": 320193, "ticker": "AAPL", "title": "Apple Inc."},
          "1": {"cik_str": 1018724, "ticker": "AMZN", "title": "AMAZON COM INC"},
          "2": {"cik_str": 1652044, "ticker": "GOOGL", "title": "Alphabet Inc."},
          "3": {"cik_str": 1652044, "ticker": "GOOG", "title": "Alphabet Inc."},
          "4": {"cik_str": 1045810, "ticker": "NVDA", "title": "NVIDIA CORP"},
          "5": {"cik_str": 1067983, "ticker": "BRK-B", "title": "BERKSHIRE HATHAWAY INC"},
          "6": {"ticker": "BROKEN"}
        }
        """;

    private static readonly DateTimeOffset Start = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);

    // CIK が銘柄から導かれる（保有が先の並びのまま。同じ CIK は 1 件。対応表に無い銘柄は飛ばす）。
    [Fact]
    public async Task CIKは収集の対象の銘柄から導かれる()
    {
        var handler = new CountingHandler(HttpStatusCode.OK, CompanyTickers);
        var set = NewSet(handler, ["AAPL", "GOOGL", "GOOG", "NVDA", "SPY"]);

        var ciks = await set.ResolveAsync();

        ciks.Should().Equal("0000320193", "0001652044", "0001045810");
        handler.Urls.Should().Equal(SecEdgarTickerCikDirectory.CompanyTickersUrl);
        handler.UserAgents.Should().Equal(UserAgent);
    }

    // 構成の固定 CIK は常に足す（導いた CIK と重なれば 1 件）。
    [Fact]
    public async Task 構成の固定CIKは導いたCIKに足す()
    {
        var set = NewSet(new CountingHandler(HttpStatusCode.OK, CompanyTickers), ["AMZN"], configured: ["320193", "1018724"]);

        var ciks = await set.ResolveAsync();

        ciks.Should().Equal("0001018724", "0000320193");
    }

    // クラス株の表記（BRK.B）も SEC の表記（BRK-B）で引ける。大文字小文字・前後空白は区別しない。
    [Fact]
    public async Task クラス株の表記の違いを吸収して引ける()
    {
        var set = NewSet(new CountingHandler(HttpStatusCode.OK, CompanyTickers), ["BRK.B", " aapl "]);

        var ciks = await set.ResolveAsync();

        ciks.Should().Equal("0001067983", "0000320193");
    }

    // 対応表は読めてから 24 時間読み直さない（1 日 1 要求）。24 時間を過ぎれば読み直す。
    [Fact]
    public async Task 対応表は24時間ごとにだけ読み直す()
    {
        var time = new ManualTimeProvider(Start);
        var handler = new CountingHandler(HttpStatusCode.OK, CompanyTickers);
        var set = NewSet(handler, ["AAPL"], time: time);

        await set.ResolveAsync();
        time.Advance(TimeSpan.FromHours(23));
        await set.ResolveAsync();
        handler.Urls.Should().HaveCount(1);

        time.Advance(TimeSpan.FromHours(1));
        await set.ResolveAsync();
        handler.Urls.Should().HaveCount(2);
    }

    // 🔴 原則 A: 読み直せなければ直前に読めた表を使い続ける（空で上書きしない）。1 時間後に読み直す。
    [Fact]
    public async Task 読み直せなければ直前の表を使い続ける()
    {
        var time = new ManualTimeProvider(Start);
        var handler = new CountingHandler(HttpStatusCode.OK, CompanyTickers);
        var set = NewSet(handler, ["AAPL"], time: time);
        await set.ResolveAsync();

        handler.Respond(HttpStatusCode.Forbidden, "");
        time.Advance(TimeSpan.FromHours(24));
        var ciks = await set.ResolveAsync();

        ciks.Should().Equal("0000320193");
        time.Advance(TimeSpan.FromMinutes(30));
        await set.ResolveAsync();
        handler.Urls.Should().HaveCount(2, "失敗の後は 1 時間待ってから読み直す");
    }

    // 🔴 原則 A: 対応表を一度も読めていなければ、固定 CIK だけで収集する。固定 CIK も空なら例外（欠測として記録させる）。
    [Fact]
    public async Task 対応表を一度も読めていなければ固定CIKだけで収集する()
    {
        var set = NewSet(new CountingHandler(HttpStatusCode.ServiceUnavailable, ""), ["AAPL"], configured: ["1018724"]);

        var ciks = await set.ResolveAsync();

        ciks.Should().Equal("0001018724");
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, "{}")]
    public async Task 対応表が不明で固定CIKも空なら欠測にする(HttpStatusCode status, string body)
    {
        var set = NewSet(new CountingHandler(status, body), ["AAPL"]);

        var act = () => set.ResolveAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*対応表*");
    }

    // 対象の銘柄が 0 件なら対応表を読まない（固定 CIK だけ。どちらも空なら 0 件＝事実としての空）。
    [Fact]
    public async Task 対象の銘柄が0件なら対応表を読まない()
    {
        var handler = new CountingHandler(HttpStatusCode.OK, CompanyTickers);
        var set = NewSet(handler, []);

        var ciks = await set.ResolveAsync();

        ciks.Should().BeEmpty();
        handler.Urls.Should().BeEmpty();
    }

    // 情報源として: 導いた CIK の提出書類を取りに行く（対応表 → 提出書類の順。同じ自制のバケットを通る）。
    [Fact]
    public async Task 情報源は導いたCIKの提出書類を取りに行く()
    {
        var handler = new CountingHandler(HttpStatusCode.OK, CompanyTickers);
        var limiter = new CountingRateLimiter();
        var http = new HttpClient(handler);
        var set = new DerivedSecEdgarCikSet(
            new FixedTargets(["AMZN"]),
            new SecEdgarTickerCikDirectory(http, UserAgent, limiter, TimeProvider.System,
                NullLogger<SecEdgarTickerCikDirectory>.Instance),
            [],
            NullLogger<DerivedSecEdgarCikSet>.Instance);
        var source = new SecEdgarInformationSource(http, UserAgent, set, limiter, NullLogger<SecEdgarInformationSource>.Instance);

        await source.FetchAsync();

        handler.Urls.Should().Equal(
            SecEdgarTickerCikDirectory.CompanyTickersUrl, "https://data.sec.gov/submissions/CIK0001018724.json");
        handler.UserAgents.Should().OnlyContain(ua => ua == UserAgent);
        limiter.Waits.Should().Be(2);
    }

    private static DerivedSecEdgarCikSet NewSet(
        CountingHandler handler, IReadOnlyList<string> targets, IReadOnlyList<string>? configured = null, TimeProvider? time = null) =>
        new(
            new FixedTargets(targets),
            new SecEdgarTickerCikDirectory(
                new HttpClient(handler), UserAgent, new CountingRateLimiter(), time ?? new ManualTimeProvider(Start),
                NullLogger<SecEdgarTickerCikDirectory>.Instance),
            configured ?? [],
            NullLogger<DerivedSecEdgarCikSet>.Instance);

    private sealed class FixedTargets(IReadOnlyList<string> targets) : ICollectionTargetSymbols
    {
        public IReadOnlyList<string> UnitedStatesTargets => targets;
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class CountingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        private readonly List<string> _urls = [];
        private readonly List<string> _userAgents = [];
        private HttpStatusCode _status = status;
        private string _body = body;

        public IReadOnlyList<string> Urls => _urls;

        public IReadOnlyList<string> UserAgents => _userAgents;

        public void Respond(HttpStatusCode newStatus, string newBody)
        {
            _status = newStatus;
            _body = newBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _urls.Add(request.RequestUri!.ToString());
            _userAgents.Add(request.Headers.UserAgent.ToString());
            var responseBody = request.RequestUri!.Host == "data.sec.gov" ? """{"name":"x","filings":{"recent":{}}}""" : _body;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }
}
