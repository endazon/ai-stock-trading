extern alias ReportWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReportWorker::ReportService.Features.Reports;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TradeDecisionService.Tests;

// 🔴 T-10-910, FR-04, FR-07, ADR-0003, #957, IADR-0408（2026-09-25 追記。T-10-800 の同型）: 判断が読む報告書の日報方針
// （GET /reports/daily-policy）に、**送り手の本物の型 `ConfirmedDailyPolicy` を送り手の実際の JSON 設定（web 既定＋列挙は文字列）で
// 直列化した応答**を読ませる。既存の `HttpDailyPolicyProviderTests` は手書きの JSON であり、送り手で `Summary` を改名しても緑のまま、
// 実行時は方針が null（空の方針でプロンプトが組まれる＝「未確定なら取引しない」の門を素通り）で読まれる。
// 送り手がその設定で出していることは報告書側の T-10-921 が本物の Program.cs で固定する。
public class ReportReadContractTests
{
    // 報告書の Program.cs（ConfigureHttpJsonOptions に JsonStringEnumConverter を足す）と同じ設定。
    private static readonly JsonSerializerOptions ReportWire =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task 日報方針は送り手の本物の型を直列化した応答から読める()
    {
        var body = JsonSerializer.Serialize(new ConfirmedDailyPolicy(new DateOnly(2026, 7, 10), "米国株の押し目買い", 3), ReportWire);
        var provider = new HttpDailyPolicyProvider(
            new HttpClient(new StubHandler(body)) { BaseAddress = new Uri("http://reports") },
            NullLogger<HttpDailyPolicyProvider>.Instance);

        var policy = await provider.GetCurrentAsync();

        policy.Should().NotBeNull();
        (policy!.Date, policy.Summary).Should().Be((new DateOnly(2026, 7, 10), "米国株の押し目買い"));
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
