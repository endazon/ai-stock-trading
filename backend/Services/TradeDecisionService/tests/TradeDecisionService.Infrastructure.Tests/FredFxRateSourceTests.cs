using System.Globalization;
using System.Net;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.TradeDecision.Infrastructure.Tests;

// FR-10, FR-17, #257, #364, IADR-0107 決定5 / IADR-0152 決定2: FRED（DEXJPUS＝JPY per USD）の FX レート源を
// fake HttpMessageHandler で検証する。実 FRED API は叩かない（IADR-0049）。
// 基準通貨は USD であるため、解決対象は **JPY** であり、レートは観測値の**逆数**（USD per JPY）である。
public class FredFxRateSourceTests
{
    private const string OkBody = """
        {"observations":[{"date":"2026-07-24","value":"152.35"},{"date":"2026-07-23","value":"151.80"}]}
        """;

    [Fact]
    public async Task 最新観測の逆数を基準通貨換算レートへ写像する()
    {
        var source = Create(new StubHandler(HttpStatusCode.OK, OkBody));

        var rate = await source.GetRateToBaseAsync(Currency.Jpy);

        rate.Should().NotBeNull();
        rate!.Quote.Should().Be(Currency.Jpy);
        rate.Base.Should().Be(Currency.Usd);
        // #364, IADR-0152 決定2: DEXJPUS は「1 USD あたりの円」。契約は「quote 1 単位あたりの基準通貨額」なので逆数。
        rate.Rate.Should().Be(1m / 152.35m);
        rate.AsOf.Should().Be(new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero));
    }

    // #364, IADR-0152 決定2: 逆数は**丸めない**。丸めると往復換算の誤差が片側へ偏り、統制の実効上限が系統的にずれる。
    [Fact]
    public async Task 逆数は丸めず観測値へ戻せる精度を保つ()
    {
        var body = """
            {"observations":[{"date":"2026-07-24","value":"163.7"}]}
            """;
        var source = Create(new StubHandler(HttpStatusCode.OK, body));

        var rate = await source.GetRateToBaseAsync(Currency.Jpy);

        rate!.Rate.Should().Be(1m / 163.7m);
        // 丸めていれば往復で誤差が残る。decimal の除算精度のまま保持していることを固定する。
        (1m / rate.Rate).Should().BeApproximately(163.7m, 0.0000000001m);
    }

    [Fact]
    public async Task 欠測は飛ばして直近の数値観測を採る()
    {
        // FRED は休日・未公表を "." で返す（DEXJPUS は営業日次系列）。
        var body = """
            {"observations":[{"date":"2026-07-25","value":"."},{"date":"2026-07-24","value":"152.35"}]}
            """;
        var source = Create(new StubHandler(HttpStatusCode.OK, body));

        var rate = await source.GetRateToBaseAsync(Currency.Jpy);

        rate!.Rate.Should().Be(1m / 152.35m);
        rate.AsOf.Should().Be(new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task 数値観測が無ければレート無し()
    {
        var body = """{"observations":[{"date":"2026-07-25","value":"."}]}""";
        var source = Create(new StubHandler(HttpStatusCode.OK, body));

        (await source.GetRateToBaseAsync(Currency.Jpy)).Should().BeNull();
    }

    [Fact]
    public async Task 非成功応答はレート無し()
    {
        var source = Create(new StubHandler(HttpStatusCode.TooManyRequests, "rate limited"));

        (await source.GetRateToBaseAsync(Currency.Jpy)).Should().BeNull();
    }

    [Fact]
    public async Task 解析できない応答はレート無し()
    {
        var source = Create(new StubHandler(HttpStatusCode.OK, "<html>error page</html>"));

        (await source.GetRateToBaseAsync(Currency.Jpy)).Should().BeNull();
    }

    [Fact]
    public async Task 通信エラーはレート無しに縮退する()
    {
        // レート無し＝非基準通貨の新規建て見送り（IADR-0107 決定3）＝安全側。
        var source = Create(new ThrowingHandler());

        (await source.GetRateToBaseAsync(Currency.Jpy)).Should().BeNull();
    }

    [Fact]
    public async Task 価格が0以下の観測は採らない()
    {
        var body = """{"observations":[{"date":"2026-07-24","value":"0"}]}""";
        var source = Create(new StubHandler(HttpStatusCode.OK, body));

        (await source.GetRateToBaseAsync(Currency.Jpy)).Should().BeNull();
    }

    [Fact]
    public async Task 基準通貨は外部へ問い合わせずレート1を返す()
    {
        var handler = new StubHandler(HttpStatusCode.OK, OkBody);
        var source = Create(handler);

        var rate = await source.GetRateToBaseAsync(Currency.Usd);

        rate!.Rate.Should().Be(1m);
        handler.Requests.Should().Be(0);
    }

    // **否定形**: 系列は USD/JPY 固定であり、対応しない通貨を推測で換算しない（誤ったレートでの発注を作らない）。
    [Fact]
    public async Task 系列が対応しない通貨は推測で換算せず外部へも出ない()
    {
        var handler = new StubHandler(HttpStatusCode.OK, OkBody);
        var source = Create(handler);

        (await source.GetRateToBaseAsync((Currency)999)).Should().BeNull();
        handler.Requests.Should().Be(0);
    }

    [Fact]
    public async Task 取得窓は設定できる最大の受容窓を賄う()
    {
        // #271, IADR-0112 決定3: 取得窓 ≥ 受容窓。FRED は欠測（休場・未公表）を "." の観測レコードとして返すため、
        // 要求件数はそのぶん消費される。件数が受容窓に届かないと、鮮度上限を広げても窓の端にある観測へ到達できず、
        // 広げた窓が部分的に無効になる。設定できる最大の受容窓（MaxAllowedRateAgeDays 日）を営業日へ換算した
        // 件数以上を要求する（リクエストは 1 回のままでレート予算・費用は変わらない）。
        var handler = new StubHandler(HttpStatusCode.OK, OkBody);

        await Create(handler).GetRateToBaseAsync(Currency.Jpy);

        var required = (int)Math.Ceiling(FxOptions.MaxAllowedRateAgeDays * 5m / 7m);
        RequestedLimit(handler.LastRequestUri!).Should().BeGreaterThanOrEqualTo(required);
    }

    [Fact]
    public async Task 送信前にレート制御を通す()
    {
        var limiter = new CountingRateLimiter();
        var source = Create(new StubHandler(HttpStatusCode.OK, OkBody), limiter);

        await source.GetRateToBaseAsync(Currency.Jpy);

        limiter.Waits.Should().Be(1);
    }

    [Fact]
    public async Task キャンセルはそのまま伝播する()
    {
        var source = Create(new StubHandler(HttpStatusCode.OK, OkBody));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => source.GetRateToBaseAsync(Currency.Jpy, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // 要求 URL のクエリから observations の要求件数（limit）を取り出す。
    private static int RequestedLimit(Uri requestUri)
    {
        var limit = requestUri.Query.TrimStart('?').Split('&')
            .Single(pair => pair.StartsWith("limit=", StringComparison.Ordinal))["limit=".Length..];
        return int.Parse(limit, CultureInfo.InvariantCulture);
    }

    private static FredFxRateSource Create(HttpMessageHandler handler, IRateLimiter? limiter = null) =>
        new(
            new HttpClient(handler),
            apiKey: "key",
            seriesId: "DEXJPUS",
            limiter ?? new CountingRateLimiter(),
            NullLogger<FredFxRateSource>.Instance);

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("接続できません");
    }

    private sealed class CountingRateLimiter : IRateLimiter
    {
        public int Waits { get; private set; }

        public Task WaitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Waits++;
            return Task.CompletedTask;
        }
    }
}
