using System.Net;
using System.Text;
using AiStockTrading.Report.Infrastructure.Composable.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.Report.Infrastructure.Tests;

// FR-10, FR-06, FR-21, UC-06, ADR-0016 決定15, #463, IADR-0181:
// 権威源（リスク管理の推定台帳）への s2s 照会を fake HttpMessageHandler で検証する（実ネットワーク不使用）。
//
// 🔴 **本アダプタの要点は「失敗の向き」である。** 同居する `HttpPeriodFillSource` は供給不達を
// **空列**へ倒すが、こちらは **`null`（未供給）** へ倒す。推定経路は実在し発火し得るため、
// 0 件と表示すると「**強制買戻しは起きていない**」と読める——計画が名指しで禁じた向きである。
//
// **隣に逆向きの前例があるため、後から「揃える」方向の整理で壊されやすい。** 本テスト群がそれを止める。
public class HttpBuyInInferenceRecordSourceTests
{
    private static readonly DateOnly From = new(2026, 8, 1);
    private static readonly DateOnly To = new(2026, 8, 8);

    // 期間は観測に覆われており、推定が 1 件ある応答。
    private const string SuppliedWithOneInference = """
    {
        "periodCovered": true,
        "observedTradingDays": ["2026-08-03","2026-08-04","2026-08-05","2026-08-06","2026-08-07"],
        "inferences": [{
            "id": "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
            "symbol": "AAPL",
            "market": 1,
            "ledgerShortQuantity": 100,
            "brokerShortQuantity": 0,
            "inFlightCloseQuantity": 0,
            "unexplainedQuantity": 100,
            "newlyInferredQuantity": 100,
            "banUntil": "2026-09-07",
            "inferredOn": "2026-08-08",
            "observedAt": "2026-08-08T06:00:00+00:00",
            "inferredAt": "2026-08-08T06:00:01+00:00"
        }]
    }
    """;

    // 期間は観測に覆われており、推定は 0 件（＝**正当な 0**）。
    private const string SuppliedWithNoInference = """
    { "periodCovered": true, "observedTradingDays": ["2026-08-03"], "inferences": [] }
    """;

    // 期間が観測に覆われていない（観測が一度も無い／途中で止まった）。**これは 0 件ではない。**
    private const string NotCovered = """
    { "periodCovered": false, "observedTradingDays": [], "inferences": [] }
    """;

    // 観測は一部の日にだけ届いた（途中で止まった期間）。**サーバが false を宣言する。**
    private const string PartiallyObserved = """
    { "periodCovered": false, "observedTradingDays": ["2026-08-03","2026-08-04"], "inferences": [] }
    """;

    private static HttpBuyInInferenceRecordSource Source(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://risk-management") },
            NullLogger<HttpBuyInInferenceRecordSource>.Instance);

    [Fact]
    public async Task 期間を要求のクエリ文字列に載せる()
    {
        var handler = new StubHandler(HttpStatusCode.OK, SuppliedWithNoInference);

        await Source(handler).GetInferencesAsync(From, To);

        handler.LastUri!.AbsolutePath.Should().Be("/risk-controls/buy-in-inferences");
        handler.LastUri.Query.Should().Contain("from=2026-08-01").And.Contain("to=2026-08-08");
    }

    // 受け入れ基準1: 推定件数を照会でき、通常表示になる。
    [Fact]
    public async Task 期間が観測に覆われていて推定があれば推定行を返す()
    {
        var handler = new StubHandler(HttpStatusCode.OK, SuppliedWithOneInference);

        var result = await Source(handler).GetInferencesAsync(From, To);

        result.Should().NotBeNull();
        result.Should().ContainSingle();
        result![0].Symbol.Should().Be("AAPL");
        result[0].NewlyInferredQuantity.Should().Be(100);
    }

    // 受け入れ基準7: 観測が届いていて推定 0 件なら **空列**（＝正当な 0）。
    // **ここを null にすると、正常に統制が働いている状態が永久に「取得できていません」になる**
    // （FR-21 の規約は両方向に効く）。
    [Fact]
    public async Task 期間が観測に覆われていて推定が無ければ空列を返す_正当な0()
    {
        var handler = new StubHandler(HttpStatusCode.OK, SuppliedWithNoInference);

        var result = await Source(handler).GetInferencesAsync(From, To);

        result.Should().NotBeNull("期間は観測に覆われており、0 件であることは事実として正しい");
        result.Should().BeEmpty();
    }

    // 受け入れ基準3（**否定形**・FR-21）: 期間が観測に覆われていなければ、台帳が空でも未供給。
    [Fact]
    public async Task 期間が観測に覆われていなければ台帳が空でも_null_を返す()
    {
        var handler = new StubHandler(HttpStatusCode.OK, NotCovered);

        var result = await Source(handler).GetInferencesAsync(From, To);

        result.Should().BeNull(
            "行数 0 は「観測が届いていない（異常）」と「観測して 0 件（正常）」を区別できない");
    }

    // **否定形・[2026-08-08 改定] の核心**: 観測が**途中で止まった**期間も未供給である。
    // 従前の「最終観測時刻が非 null か」では、この形が「正当な 0」として素通りしていた
    // （計画 FR-21 が名指しした失敗モード 2）。
    [Fact]
    public async Task 観測が途中で止まった期間は_null_を返す()
    {
        var handler = new StubHandler(HttpStatusCode.OK, PartiallyObserved);

        var result = await Source(handler).GetInferencesAsync(From, To);

        result.Should().BeNull("一部の日だけ観測されていても、期間は覆われていない");
    }

    // 旧版 Risk（本項目を返さない）への耐性。**項目の欠落を「覆っている」と読まない。**
    [Fact]
    public async Task 期間判定を返さない旧版応答では_null_を返す()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{ "inferences": [] }""");

        var result = await Source(handler).GetInferencesAsync(From, To);

        result.Should().BeNull();
    }

    // 受け入れ基準2（**否定形・最重要**）: 照会が失敗したら **すべて null**。空列（＝0 件）へ倒さない。
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task 非2xx応答は_null_を返す_0件へ倒さない(HttpStatusCode status)
    {
        var handler = new StubHandler(status, "");

        var result = await Source(handler).GetInferencesAsync(From, To);

        result.Should().BeNull("照会できなかったことは「推定 0 件」ではない");
    }

    [Fact]
    public async Task 例外は_null_を返し送出しない()
    {
        var result = await Source(new ThrowingHandler()).GetInferencesAsync(From, To);

        result.Should().BeNull();
    }

    [Fact]
    public async Task タイムアウトは_null_を返す()
    {
        var http = new HttpClient(new DelayingHandler(TimeSpan.FromSeconds(5)))
        {
            BaseAddress = new Uri("http://risk-management"),
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        var source = new HttpBuyInInferenceRecordSource(
            http, NullLogger<HttpBuyInInferenceRecordSource>.Instance);

        var result = await source.GetInferencesAsync(From, To);

        result.Should().BeNull();
    }

    [Fact]
    public async Task 不正な応答本文は_null_を返す()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "null");

        var result = await Source(handler).GetInferencesAsync(From, To);

        result.Should().BeNull();
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("接続できません");
    }

    private sealed class DelayingHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(NotCovered, Encoding.UTF8, "application/json"),
            };
        }
    }
}
