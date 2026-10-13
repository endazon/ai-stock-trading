using System.Net;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Fx;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.Fx;

// FR-10, FR-17, #381, ADR-0022 決定1, IADR-0194: 日銀「外国為替市況（日次）」の FX レート源を
// fake HttpMessageHandler で検証する。実 API は叩かない（IADR-0049。外部依存で CI を落とさない）。
//
// 応答本文は **2026-08-15 に実 API を叩いて得た実物の形**に合わせてある
// （db=fm08 / code=FXERD04,FXERD05・format=json&lang=en）。作業仕様書 20260815_381 の「実測」節を参照。
public class BojFxRateSourceTests
{
    // 実測の応答そのままの形。末尾 3 期（08-13・08-14・08-15）が null であるのが実際の姿である
    // ——収録は「翌々営業日 8:50 頃」であり、当日・前営業日が null なのは**正常**である。
    // 08-11（火）は山の日で null。
    private const string OkBody = """
        {"RESULTSET":[{"SERIES_CODE":"FXERD04",
          "NAME_OF_TIME_SERIES":"US.Dollar/Yen Spot Rate at 17:00 in JST, Tokyo Market",
          "LAST_UPDATE":20260814,
          "VALUES":{"SURVEY_DATES":[20260810,20260811,20260812,20260813,20260814,20260815],
                    "VALUES":[158.49,null,159.38,null,null,null]}}]}
        """;

    [Fact]
    public async Task 最新観測の逆数を基準通貨換算レートへ写像する()
    {
        var source = Create(new StubHandler(HttpStatusCode.OK, OkBody));

        var rate = await source.GetRateToBaseAsync(Currency.Jpy);

        rate.Should().NotBeNull();
        rate!.Quote.Should().Be(Currency.Jpy);
        rate.Base.Should().Be(Currency.Usd);
        // ADR-0022 決定1 の系列は「1 USD あたりの円」。契約は「quote 1 単位あたりの基準通貨額」なので逆数。
        rate.Rate.Should().Be(1m / 159.38m);
        // 日銀の統計は日本時間で公表される（JST は夏時間を持たない）。
        rate.AsOf.Should().Be(new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.FromHours(9)));
    }

    // 🔴 ADR-0022 決定1 の落とし穴 3。**受け入れ基準そのもの**である。
    // 収録は翌々営業日 8:50 頃であり、当日・前営業日が null なのは平常時の姿である。
    // これを異常として扱うと、平常時に毎日レート無し＝非基準通貨の新規建てが常時止まる。
    [Fact]
    public async Task 当日と前営業日がnullでも異常とせず値のある直近を採る()
    {
        var source = Create(new StubHandler(HttpStatusCode.OK, OkBody));

        var rate = await source.GetRateToBaseAsync(Currency.Jpy);

        // 末尾 3 期が null でも、その手前（08-12）を採れている。
        rate.Should().NotBeNull();
        rate!.AsOf.Date.Should().Be(new DateTime(2026, 8, 12));
    }

    // 🔴 **否定形・受け入れ基準**: 中心相場（FXERD05）は別系列であり、決定1 が指す仲値ではない。
    // 中心相場は「その日に最も取引の多かった出来値」であって、仲値なのは 17 時時点（FXERD04）だけである。
    //
    // **取り違えても動作は正常に見える**——もっともらしい値が返るためテストでしか捕まらない。
    // 実測（2026-08-15）では同じ 2026-08-12 で FXERD04=159.38 / FXERD05=159.35 と**実際に値が違う**。
    [Fact]
    public async Task 中心相場FXERD05は仲値ではないため採らない()
    {
        // 実 API は code に複数系列を渡せば両方返す。要求した系列だけを採ることを固定する。
        var body = """
            {"RESULTSET":[
              {"SERIES_CODE":"FXERD05","NAME_OF_TIME_SERIES":"US.Dollar/Yen Central Rate, Tokyo Market",
               "LAST_UPDATE":20260814,
               "VALUES":{"SURVEY_DATES":[20260812],"VALUES":[159.35]}},
              {"SERIES_CODE":"FXERD04","NAME_OF_TIME_SERIES":"US.Dollar/Yen Spot Rate at 17:00 in JST, Tokyo Market",
               "LAST_UPDATE":20260814,
               "VALUES":{"SURVEY_DATES":[20260812],"VALUES":[159.38]}}]}
            """;
        var source = Create(new StubHandler(HttpStatusCode.OK, body));

        var rate = await source.GetRateToBaseAsync(Currency.Jpy);

        // 応答の**先頭**が中心相場でも、要求した FXERD04 を採る（順序に依存しない）。
        rate!.Rate.Should().Be(1m / 159.38m, "仲値は 17 時時点（FXERD04）であり中心相場（FXERD05）ではない");
        rate.Rate.Should().NotBe(1m / 159.35m);
    }

    // **否定形**: 要求した系列が応答に含まれないなら、別系列で代用しない。
    [Fact]
    public async Task 要求した系列が応答に無ければ別系列で代用しない()
    {
        var body = """
            {"RESULTSET":[{"SERIES_CODE":"FXERD05","LAST_UPDATE":20260814,
              "VALUES":{"SURVEY_DATES":[20260812],"VALUES":[159.35]}}]}
            """;
        var source = Create(new StubHandler(HttpStatusCode.OK, body));

        (await source.GetRateToBaseAsync(Currency.Jpy)).Should().BeNull();
    }

    // 🔴 ADR-0022 決定1 の落とし穴 1: code には**系列コード**（FXERD04）を渡す。
    // 画面で使うデータコード（FM08'FXERD04）を渡すと API がエラーになる。DB 名は db=fm08 として別に渡す。
    [Fact]
    public async Task 系列コードとDB名を別のパラメータとして渡す()
    {
        var handler = new StubHandler(HttpStatusCode.OK, OkBody);

        await Create(handler).GetRateToBaseAsync(Currency.Jpy);

        var query = handler.LastRequestUri!.Query;
        query.Should().Contain("db=fm08");
        query.Should().Contain("code=FXERD04");
        // データコード（FM08'FXERD04）を渡していないこと。渡すと API がエラーを返す。
        query.Should().NotContain("FM08");
    }

    // 決定2: format=json&lang=en を使う。#381 の受け入れ基準は「CP932 デコード」と書いているが、
    // CP932 は format=csv&lang=jp の話であり、JSON/en では現れない（実測 2026-08-15・純 ASCII）。
    [Fact]
    public async Task 応答形式はJSONの英語版を要求する()
    {
        var handler = new StubHandler(HttpStatusCode.OK, OkBody);

        await Create(handler).GetRateToBaseAsync(Currency.Jpy);

        handler.LastRequestUri!.Query.Should().Contain("format=json").And.Contain("lang=en");
    }

    [Fact]
    public async Task 全期がnullならレート無し()
    {
        var body = """
            {"RESULTSET":[{"SERIES_CODE":"FXERD04","LAST_UPDATE":20260814,
              "VALUES":{"SURVEY_DATES":[20260812,20260813],"VALUES":[null,null]}}]}
            """;
        var source = Create(new StubHandler(HttpStatusCode.OK, body));

        (await source.GetRateToBaseAsync(Currency.Jpy)).Should().BeNull();
    }

    [Fact]
    public async Task 価格が0以下の観測は採らない()
    {
        var body = """
            {"RESULTSET":[{"SERIES_CODE":"FXERD04","LAST_UPDATE":20260814,
              "VALUES":{"SURVEY_DATES":[20260812],"VALUES":[0]}}]}
            """;
        var source = Create(new StubHandler(HttpStatusCode.OK, body));

        (await source.GetRateToBaseAsync(Currency.Jpy)).Should().BeNull();
    }

    // 日付が読めない形は推測で日付を作らない（誤った鮮度で発注させない）。
    [Fact]
    public async Task 日付が読めない観測は採らない()
    {
        var body = """
            {"RESULTSET":[{"SERIES_CODE":"FXERD04","LAST_UPDATE":20260814,
              "VALUES":{"SURVEY_DATES":["2026-08-12"],"VALUES":[159.38]}}]}
            """;
        var source = Create(new StubHandler(HttpStatusCode.OK, body));

        (await source.GetRateToBaseAsync(Currency.Jpy)).Should().BeNull();
    }

    [Fact]
    public async Task 非成功応答はレート無し()
    {
        var source = Create(new StubHandler(HttpStatusCode.ServiceUnavailable, "unavailable"));

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
        // レート無し＝フォールバックが試され、それも駄目なら新規建て見送り（安全側）。
        var source = Create(new ThrowingHandler());

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

    // **否定形**: 系列は USD/JPY 固定であり、対応しない通貨を推測で換算しない。
    [Fact]
    public async Task 系列が対応しない通貨は推測で換算せず外部へも出ない()
    {
        var handler = new StubHandler(HttpStatusCode.OK, OkBody);
        var source = Create(handler);

        (await source.GetRateToBaseAsync((Currency)999)).Should().BeNull();
        handler.Requests.Should().Be(0);
    }

    // 日銀は数値上限を示さず「短時間における高頻度のアクセス」を禁止行為とするため、送信前に自制する。
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

    // 期間パラメータは指定しない。日次・週次も月次形式 YYYYMM で指定する仕様であり、
    // 推測すると静かに欠測する（BojInformationSource・IADR-0064 と同じ判断）。
    [Fact]
    public async Task 期間パラメータを推測で指定しない()
    {
        var handler = new StubHandler(HttpStatusCode.OK, OkBody);

        await Create(handler).GetRateToBaseAsync(Currency.Jpy);

        handler.LastRequestUri!.Query.Should().NotContain("startDate").And.NotContain("endDate");
    }

    // --- #1331, IADR-0531: 取得の成否をログから読み分けられること -------------------------------------
    //
    // PoC 2026-10-12（スポーツの日）に「JPY の観測が 10/07 から進まない」と起票された。実 API（2026-10-13 21:19 JST）の
    // 末尾は下のとおりで、**10/08 の値は 10/13（火）に収録されていた**（LAST_UPDATE 20261013）。翌々営業日の収録に
    // 祝日が挟まっただけで、取得は成功していた。**成功時に何も出していなかったため、鮮度の警告しか見えず**
    // 「取れていない」と「取れているが収録待ち」をログから区別できなかった。
    private const string Body20261013 = """
        {"STATUS":200,"MESSAGEID":"M181000I","MESSAGE":"Successfully completed",
         "RESULTSET":[{"SERIES_CODE":"FXERD04",
          "NAME_OF_TIME_SERIES":"US.Dollar/Yen Spot Rate at 17:00 in JST, Tokyo Market",
          "LAST_UPDATE":20261013,
          "VALUES":{"SURVEY_DATES":[20261002,20261003,20261004,20261005,20261006,20261007,20261008],
                    "VALUES":[157.58,null,null,157.66,158.12,158.11,158.19]}}]}
        """;

    [Fact]
    public async Task 取得に成功したら観測日と系列の最終更新をInformationで残す()
    {
        var log = new RecordingLogger();
        var source = Create(new StubHandler(HttpStatusCode.OK, Body20261013), logger: log);

        var rate = await source.GetRateToBaseAsync(Currency.Jpy);

        rate!.AsOf.Should().Be(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.FromHours(9)));
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information)
            .Which.Message.Should().Contain("日銀 FX を取得しました").And.Contain("2026-10-08").And.Contain("20261013");
        log.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning, "成功は警告ではない（本物の警告を埋めない）");
    }

    // 🔴 HttpClient のタイムアウトは OperationCanceledException（TaskCanceledException）で届く。
    // 従来は「取り消し」として素通しし、FallbackFxRateSource も取り消しと読んで FRED を試さず、
    // 日銀側の警告も出なかった。呼び出し側が取り消していない限り、通信の失敗として扱う。
    [Fact]
    public async Task タイムアウトはレート無しに縮退し警告を残す()
    {
        var log = new RecordingLogger();
        var source = Create(new TimeoutHandler(), logger: log);

        (await source.GetRateToBaseAsync(Currency.Jpy)).Should().BeNull();
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("タイムアウト");
    }

    [Fact]
    public async Task 日銀がタイムアウトしたらフォールバックの源を試す()
    {
        var fallback = new FixedSource(new FxRate(Currency.Jpy, Currency.Usd, 1m / 157.81m,
            new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero)));
        var composite = new FallbackFxRateSource(
            [("boj", Create(new TimeoutHandler())), ("fred", fallback)],
            NullLogger<FallbackFxRateSource>.Instance);

        var rate = await composite.GetRateToBaseAsync(Currency.Jpy);

        rate!.Rate.Should().Be(1m / 157.81m);
        fallback.Calls.Should().Be(1);
    }

    // 否定形: 呼び出し側の取り消しは従来どおり伝播させる（取り消したのに外部通信を続けない）。
    [Fact]
    public async Task 呼び出し側が取り消したときのタイムアウト形の例外は伝播する()
    {
        using var cts = new CancellationTokenSource();
        var source = Create(new TimeoutHandler(onSend: cts.Cancel));

        var act = () => source.GetRateToBaseAsync(Currency.Jpy, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task 系列の無い応答はSTATUSとMESSAGEIDを警告に残す()
    {
        var log = new RecordingLogger();
        var source = Create(
            new StubHandler(HttpStatusCode.OK,
                """{"STATUS":400,"MESSAGEID":"M181005E","MESSAGE":"Invalid parameter","RESULTSET":[]}"""),
            logger: log);

        (await source.GetRateToBaseAsync(Currency.Jpy)).Should().BeNull();
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("400").And.Contain("M181005E");
    }

    private static BojFxRateSource Create(
        HttpMessageHandler handler, IRateLimiter? limiter = null, ILogger<BojFxRateSource>? logger = null) =>
        new(
            new HttpClient(handler),
            seriesCode: BojFxRateSource.DefaultSeriesCode,
            limiter ?? new CountingRateLimiter(),
            logger ?? NullLogger<BojFxRateSource>.Instance);

    // HttpClient.Timeout 超過と同じ形（TaskCanceledException の内側に TimeoutException）で失敗する。
    private sealed class TimeoutHandler(Action? onSend = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onSend?.Invoke();
            throw new TaskCanceledException("timeout", new TimeoutException());
        }
    }

    private sealed class FixedSource(FxRate rate) : AiStockTrading.Shared.Contracts.Ports.IFxRateSource
    {
        public int Calls { get; private set; }

        public Task<FxRate?> GetRateToBaseAsync(Currency quote, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<FxRate?>(rate);
        }
    }

    private sealed class RecordingLogger : ILogger<BojFxRateSource>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

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
            throw new HttpRequestException("boom");
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
