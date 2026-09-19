using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReportService.Features.Reports;
using ReportService.Hosted;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-07, NFR-05, UC-03〜05, ADR-0003, #840, IADR-0352: **再起動直後に Keycloak がまだ起動していない**状況を
// Program.cs の配線そのもの（名前付き HttpClient の鎖・供給元・生成器・常駐）で再現する。
//
// 門（トークンを取れなければ送信しない）と見送り（縮退した報告書を作らない）は、Program.cs の登録順と DI の
// 結線にしか現れない。部品単体の試験（ReportDependencyHandlerTests / ReportAutoGeneratorDependencyRetryTests）が
// 緑でも、鎖へ挿し忘れれば本番では何も起きない——**ここが本件（#840）の実測を再現する試験である。**
//
// 🔴 本試験は本変更前から在る公開面（常駐の RunOnceAsync・IReportStore・本文）だけで書いてある。
// 変更前のコードでも**コンパイルは通り、実行で落ちる**（＝修正が効いていることの証拠になる）。
public class ReportDependencyWiringTests(ReportWorkerWebApplicationFactory factory)
    : IClassFixture<ReportWorkerWebApplicationFactory>
{
    private const string TokenJson = """{"access_token":"T","token_type":"Bearer","expires_in":300}""";

    private const string PositionsJson =
        """[{"symbol":"AAPL","market":1,"side":0,"quantity":1,"entryPrice":190.5,"stopLossPrice":180.0}]""";

    // Keycloak の token エンドポイント。Up=false の間は接続できない（起動中）。
    private sealed class Keycloak : HttpMessageHandler
    {
        public bool Up { get; set; }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (!Up)
                throw new HttpRequestException("Connection refused (keycloak:8080)");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(TokenJson, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    // 台帳（リスク管理・監査）。実物と同じく、Authorization の無い要求は 401 で拒否する。
    private sealed class Ledger : HttpMessageHandler
    {
        public List<(string Path, string? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Requests)
                Requests.Add((path, request.Headers.Authorization?.ToString()));

            if (request.Headers.Authorization is null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

            var body = path switch
            {
                "/risk-controls/open-positions" => PositionsJson,
                "/risk-controls/session-uptime" => """{"days":[]}""",
                "/risk-controls/stage-gate" => """{"currentStage":0}""",
                _ => "[]",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    // 実測（#840）では散文 LLM（MSP レルムのトークン）も同じ形で 401 → プレースホルダへ倒れていた。
    // "report-llm" の鎖にも門が挿さっていることを、Program.cs の配線で確かめる。
    [Fact]
    public async Task LLM_向けのトークンを取れなければ_report_llm_は認証なしで送信しない()
    {
        var keycloak = new Keycloak { Up = false };
        var gateway = new Ledger();

        using var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("LlmGateway:Auth:TokenEndpoint", "http://keycloak:8080/realms/platform/protocol/openid-connect/token");
            b.UseSetting("LlmGateway:Auth:ClientId", "ai-stock-trading-llm-caller");
            b.UseSetting("LlmGateway:Auth:ClientSecret", "msp-realm-secret");
            b.ConfigureServices(services =>
            {
                services.AddHttpClient("report-llm").ConfigurePrimaryHttpMessageHandler(() => gateway);
                services.AddHttpClient(AiStockTrading.Shared.Contracts.Llm.LlmGatewayAuth.TokenClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => keycloak);
            });
        });

        var http = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("report-llm");
        http.BaseAddress = new Uri("http://llm-gateway");

        var send = () => http.PostAsync(
            "/complete", new StringContent("""{"prompt":"p"}""", System.Text.Encoding.UTF8, "application/json"));

        // 送信せずに通信失敗として返す（判定器 HttpReportNarrativeDrafter は例外をプレースホルダ散文へ倒す）。
        await send.Should().ThrowAsync<HttpRequestException>();
        keycloak.Calls.Should().BeGreaterThan(0);
        // 🔴 否定形: 認証なしの要求を LLM ゲートウェイへ投げていない。
        gateway.Requests.Should().BeEmpty();

        // 対照: トークンが取れれば付けて送る（門が常に止めているのではない）。
        keycloak.Up = true;
        using var response = await http.PostAsync(
            "/complete", new StringContent("""{"prompt":"p"}""", System.Text.Encoding.UTF8, "application/json"));
        gateway.Requests.Should().ContainSingle().Which.Authorization.Should().Be("Bearer T");
    }

    [Fact]
    public async Task 再起動直後にトークンを取れない巡回は縮退した報告書を作らず_取れた巡回で建玉つきの報告書を出す()
    {
        var keycloak = new Keycloak { Up = false };
        var ledger = new Ledger();

        using var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Reports:AutoGeneration:Enabled", "true");
            b.UseSetting("RiskManagement:BaseUrl", "http://risk-management-service");
            b.UseSetting("Audit:BaseUrl", "http://audit-service");
            // token エンドポイントは明示する（Program.cs は登録時に構成を読むため、試験用ファクトリが後から足す
            // Auth:Authority からの導出には頼れない）。
            b.UseSetting(
                "ServiceAuth:TokenEndpoint", "http://keycloak:8080/realms/ai-stock-trading/protocol/openid-connect/token");
            b.UseSetting("ServiceAuth:ClientId", "ai-stock-trading-svc");
            b.UseSetting("ServiceAuth:ClientSecret", "dev-only-secret");
            b.ConfigureServices(services =>
            {
                services.AddHttpClient("risk-ledger").ConfigurePrimaryHttpMessageHandler(() => ledger);
                services.AddHttpClient("audit-ledger").ConfigurePrimaryHttpMessageHandler(() => ledger);
                services.AddHttpClient("ai-stock-trading-service-token").ConfigurePrimaryHttpMessageHandler(() => keycloak);
            });
        });
        var scheduler = app.Services.GetServices<IHostedService>().OfType<ReportAutoGenerationService>().Single();

        // ---- 1 巡回目: Keycloak がまだ起動していない ------------------------------------------------
        await scheduler.RunOnceAsync(CancellationToken.None);

        keycloak.Calls.Should().BeGreaterThan(0, "トークンの取得は試みている（試みずに諦めたのではない）");
        using (var scope = app.Services.CreateScope())
        {
            // 🔴 否定形: 入力が広範に未供給の報告書が、承認待ちに並んでいない。
            scope.ServiceProvider.GetRequiredService<IReportStore>().List().Should().BeEmpty();
        }

        // 🔴 否定形: 認証なしの要求を台帳へ 1 件も投げていない（本変更前は全件が 401 を踏んでいた）。
        ledger.Requests.Should().BeEmpty();

        // ---- 2 巡回目: Keycloak が立ち上がった -----------------------------------------------------
        keycloak.Up = true;
        await scheduler.RunOnceAsync(CancellationToken.None);

        using (var scope = app.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IReportStore>();
            var reports = store.List();

            reports.Should().NotBeEmpty();
            reports.Should().OnlyContain(r => store.GetReview(r.PeriodKey)!.State == Domain.ReviewState.PendingApproval);

            // 日報は常に直近営業日が対象になる。建玉は台帳から取れており、本文に載っている。
            var daily = reports.Where(r => r.Kind == Domain.ReportKind.Daily).Should().ContainSingle().Subject;
            daily.Body.Should().Contain("AAPL");
            daily.Body.Should().NotContain("建玉を照会できませんでした");
        }

        ledger.Requests.Should().NotBeEmpty();
        ledger.Requests.Should().OnlyContain(r => r.Authorization == "Bearer T");
    }
}
