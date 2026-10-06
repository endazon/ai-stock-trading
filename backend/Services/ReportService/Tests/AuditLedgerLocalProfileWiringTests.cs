using System.Net;
using System.Web;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Features.Reports;
using ReportService.Infrastructure.ExternalServices;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-11, FR-16, FR-10, #1178: 経路B（values-local）で報告書を監査台帳へ REST で結線した構成
// （`Audit__BaseUrl=http://audit-service:8080` ＋ 既存の `ServiceAuth__*`）を**本番の Program.cs の組み立て**で再現し、
// 日報の 6 つの供給元（為替の情報源の状態・LLM 利用実績・借株料・判断根拠・損切りの実行機構〔承認の記録／発注執行の解決結果〕）が
// 未供給の実装ではなく監査台帳の実装になり、**サービストークンつきで** `GET /audit/events/by-type` を引いて実値（非 null）を返すことを固定する。
//
// 🔴 型を見るだけでなく実際に呼ぶ（配線を外しても緑になる形を塞ぐ）。宛先・経路・種別・Authorization を要求ごとに見る。
// 構成は `UseSetting`（ホスト構成）で与える —— Program.cs が登録時に読む値であり `ConfigureAppConfiguration` では届かない。
public class AuditLedgerLocalProfileWiringTests(ReportWorkerWebApplicationFactory factory)
    : IClassFixture<ReportWorkerWebApplicationFactory>
{
    private const string TokenJson = """{"access_token":"T","token_type":"Bearer","expires_in":300}""";

    // values-local.yaml の report の env と同じ値（#1178）。
    private const string LocalAuditBaseUrl = "http://audit-service:8080";

    private sealed class Keycloak : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(TokenJson, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    // 監査台帳。実物（OwnerOrService）と同じく、Authorization の無い要求は 401 で拒否する。
    private sealed class AuditLedger : HttpMessageHandler
    {
        public List<(Uri Uri, string? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString()));

            return Task.FromResult(request.Headers.Authorization is null
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
                });
        }
    }

    [Fact]
    public async Task 経路Bの結線で_6_つの供給元が監査台帳の実装になり_サービストークンつきで_by_type_を引いて実値を返す()
    {
        var ledger = new AuditLedger();
        using var configured = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Audit:BaseUrl", LocalAuditBaseUrl);
            b.UseSetting(
                "ServiceAuth:TokenEndpoint", "http://keycloak:8080/realms/platform/protocol/openid-connect/token");
            b.UseSetting("ServiceAuth:ClientId", "ai-stock-trading-svc");
            b.UseSetting("ServiceAuth:ClientSecret", "dev-only-secret");
            b.ConfigureServices(services =>
            {
                services.AddHttpClient("audit-ledger").ConfigurePrimaryHttpMessageHandler(() => ledger);
                services.AddHttpClient("ai-stock-trading-service-token").ConfigurePrimaryHttpMessageHandler(() => new Keycloak());
            });
        });
        var sp = configured.Services;
        var from = new DateOnly(2026, 10, 5);
        var to = new DateOnly(2026, 10, 6);

        sp.GetService<AuditGrpcTransport>().Should().BeNull("経路B は REST で結線する（gRPC の宣言は無い）");
        sp.GetRequiredService<IFxSourceStatusSource>().Should().BeOfType<HttpFxSourceStatusSource>();
        sp.GetRequiredService<ILlmUsageRecordSource>().Should().BeOfType<HttpLlmUsageRecordSource>();
        sp.GetRequiredService<IBorrowFeeRecordSource>().Should().BeOfType<HttpBorrowFeeRecordSource>();
        sp.GetRequiredService<ITradeRationaleSource>().Should().BeOfType<HttpTradeRationaleSource>();
        sp.GetRequiredService<IStopLossMethodUsageSource>().Should().BeOfType<HttpStopLossMethodUsageSource>();
        sp.GetRequiredService<IStopLossMethodResolutionSource>().Should().BeOfType<HttpStopLossMethodResolutionSource>();

        // 🔴 否定形: 6 つとも未供給（null）ではない。
        (await sp.GetRequiredService<IFxSourceStatusSource>().GetStatusAsync(from, to)).Should().NotBeNull();
        (await sp.GetRequiredService<ILlmUsageRecordSource>().GetUsageAsync(from, to)).Should().NotBeNull();
        (await sp.GetRequiredService<IBorrowFeeRecordSource>().GetBorrowFeesAsync(from, to)).Should().NotBeNull();
        (await sp.GetRequiredService<ITradeRationaleSource>().GetRationalesAsync(from, to)).Should().NotBeNull();
        (await sp.GetRequiredService<IStopLossMethodUsageSource>().GetUsageAsync(from, to)).Should().NotBeNull();
        (await sp.GetRequiredService<IStopLossMethodResolutionSource>().GetResolutionsAsync(from, to)).Should().NotBeNull();

        ledger.Requests.Should().HaveCount(6, "6 つの供給元がそれぞれ 1 回ずつ監査台帳を引いた");
        ledger.Requests.Should().OnlyContain(r =>
            r.Uri.Host == "audit-service" && r.Uri.Port == 8080 && r.Uri.AbsolutePath == "/audit/events/by-type");
        ledger.Requests.Should().OnlyContain(r => r.Authorization == "Bearer T", "OwnerOrService の s2s 分岐へサービストークンで入る");

        // 引く種別は、生産者（取引判断・リスク管理・発注執行・報告書）が発行し監査台帳が保存する事象の名前と一致する。
        var types = ledger.Requests
            .Select(r => HttpUtility.ParseQueryString(r.Uri.Query)["types"]!)
            .ToList();
        types.Should().BeEquivalentTo(
        [
            "FxRateSourceFellBack,FxRateSourcePrimaryRestored,FxRateStale,PositionClosedWithStaleFxRate,FxRateSourceUsed",
            "LlmCostIncurred,LlmFallbackFired,TradeDecisionSkipped",
            "BorrowFeeAccrued,BorrowFeeAccrualUnavailable",
            "TradeDecisionMade",
            "OrderApproved",
            "StopLossMethodResolved",
        ]);
    }
}
