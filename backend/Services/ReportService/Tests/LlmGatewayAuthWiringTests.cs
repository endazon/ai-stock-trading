using System.Net;
using AiStockTrading.Shared.Contracts.Llm;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ReportService.Tests;

// NFR-05, FR-06/16, #724, IADR-0323: **報告書散文の `/complete` への s2s トークン付与が Program.cs の配線を
// 通ること**を固定する。付与の 1 行は名前付きクライアント "report-llm" の登録にしか無いため、
// アダプタ単体テスト（`HttpReportNarrativeDrafterTests`）では観測できない。
//
// 🔴 **陽性だけの試験は今日でも通り、何も証明しない。** 資格情報が無ければ付かないこと（陰性対照）を対で置く。
public class LlmGatewayAuthWiringTests(ReportWorkerWebApplicationFactory factory)
    : IClassFixture<ReportWorkerWebApplicationFactory>
{
    private const string TokenJson = """{"access_token":"T","token_type":"Bearer","expires_in":300}""";

    // MSP `platform` レルムの token エンドポイント（AST レルムではない・IADR-0093 決定3 と同じ分離）。
    private const string MspTokenEndpoint = "http://keycloak:8080/realms/platform/protocol/openid-connect/token";

    // "report-llm" で実際に 1 回送信し、ゲートウェイ側が観測したヘッダを返す
    //（Program.cs が組んだハンドラの鎖をそのまま通す。一次ハンドラだけ差し替えている）。
    private static async Task<(RecordingHandler Gateway, RecordingHandler Token)> SendAsync(
        ReportWorkerWebApplicationFactory factory, IReadOnlyDictionary<string, string?> settings)
    {
        var gateway = new RecordingHandler(HttpStatusCode.OK,
            """{"text":"所感","model":"claude-sonnet-5","inputTokens":1,"outputTokens":1,"sent":true}""");
        var token = new RecordingHandler(HttpStatusCode.OK, TokenJson);

        using var configured = factory.WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings)
                b.UseSetting(key, value);

            b.ConfigureServices(services =>
            {
                services.AddHttpClient("report-llm").ConfigurePrimaryHttpMessageHandler(() => gateway);
                services.AddHttpClient(LlmGatewayAuth.TokenClientName).ConfigurePrimaryHttpMessageHandler(() => token);
            });
        });

        var http = configured.Services.GetRequiredService<IHttpClientFactory>().CreateClient("report-llm");
        http.BaseAddress = new Uri("http://llm-gateway");
        using var response = await http.PostAsync(
            "/complete", new StringContent("""{"prompt":"p"}""", System.Text.Encoding.UTF8, "application/json"));

        return (gateway, token);
    }

    // ---- 陽性: 資格情報があれば Bearer が付く（この試験が突然変異検査の受け皿である） ----------

    [Fact]
    public async Task LlmGatewayAuth設定時は_complete_送信に_MSPレルムの_Bearer_が付与される()
    {
        var (gateway, token) = await SendAsync(factory, new Dictionary<string, string?>
        {
            [$"{LlmGatewayAuth.SectionName}:TokenEndpoint"] = MspTokenEndpoint,
            [$"{LlmGatewayAuth.SectionName}:ClientId"] = "ai-stock-trading-kb-writer",
            [$"{LlmGatewayAuth.SectionName}:ClientSecret"] = "msp-realm-secret",
        });

        gateway.LastAuthorization.Should().Be("Bearer T");
        // 付与が「たまたま残っていたヘッダ」ではなく、実際に token エンドポイントを叩いて得たものであること。
        token.CallCount.Should().Be(1);
    }

    // Authority からの導出経路（values-local が使う設定点。TokenEndpoint を書かずに Authority だけ与える）。
    [Fact]
    public async Task Authority指定でも_MSPレルムの_token_エンドポイントを導出して付与する()
    {
        var (gateway, token) = await SendAsync(factory, new Dictionary<string, string?>
        {
            [$"{LlmGatewayAuth.SectionName}:Authority"] = "http://keycloak:8080/realms/platform",
            [$"{LlmGatewayAuth.SectionName}:ClientId"] = "ai-stock-trading-kb-writer",
            [$"{LlmGatewayAuth.SectionName}:ClientSecret"] = "msp-realm-secret",
        });

        gateway.LastAuthorization.Should().Be("Bearer T");
        token.LastRequestUri.Should().Be(MspTokenEndpoint);
    }

    // ---- 陰性対照 ---------------------------------------------------------------------------

    // 未設定なら何も付けない＝本変更前とバイト等価。これが無いと陽性試験は「常に何か付く」実装でも緑になる。
    [Fact]
    public async Task 陰性対照_LlmGatewayAuth未設定なら_Authorization_を付けない()
    {
        var (gateway, _) = await SendAsync(factory, new Dictionary<string, string?>());

        gateway.CallCount.Should().Be(1);
        gateway.LastAuthorization.Should().BeNull();
    }

    // 🔴 レルム取り違えの防止。AST レルムの ServiceAuth（report は risk-ledger / audit-ledger で使う）が
    // 同居していても "report-llm" には漏れない。漏らすと MSP へ AST レルムのトークンを出して 401 になる。
    [Fact]
    public async Task 陰性対照_ASTレルムのServiceAuthが同居しても_report_llm_へ漏れない()
    {
        var (gateway, _) = await SendAsync(factory, new Dictionary<string, string?>
        {
            ["Auth:Authority"] = "http://keycloak:8080/realms/ai-stock-trading",
            ["ServiceAuth:ClientId"] = "ai-stock-trading-svc",
            ["ServiceAuth:ClientSecret"] = "ast-realm-secret",
        });

        gateway.CallCount.Should().Be(1);
        gateway.LastAuthorization.Should().BeNull();
    }

    // 資格情報が半端（ClientSecret 欠如）なら無効＝安全側。
    [Fact]
    public async Task 陰性対照_ClientSecret欠如は無効でトークンを付けない()
    {
        var (gateway, _) = await SendAsync(factory, new Dictionary<string, string?>
        {
            [$"{LlmGatewayAuth.SectionName}:Authority"] = "http://keycloak:8080/realms/platform",
            [$"{LlmGatewayAuth.SectionName}:ClientId"] = "ai-stock-trading-kb-writer",
        });

        gateway.LastAuthorization.Should().BeNull();
    }

    // 送信要求の Authorization・宛先を記録するだけのハンドラ。
    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastAuthorization { get; private set; }

        public string? LastRequestUri { get; private set; }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri?.ToString();
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
