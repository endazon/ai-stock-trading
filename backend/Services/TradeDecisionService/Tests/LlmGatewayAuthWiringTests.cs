using System.Net;
using AiStockTrading.Shared.Contracts.Llm;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// NFR-05, FR-04, #724, IADR-0323: **`/complete` への s2s トークン付与が composition root の配線を通ること**を固定する。
//
// なぜアダプタ単体テストでは足りないか:
//   `HttpLlmCompletionClient` は `HttpClient` を受け取るだけで、トークンは名前付きクライアント "llm" の
//   `DelegatingHandler` が付ける。付与の 1 行は **Program.cs にしか無い**ため、Program.cs を起こさない試験は
//   「付いていない配線」を緑にしてしまう（IADR-0212 で同型の見落としが実測されている）。
//
// 🔴 **陽性だけの試験は今日でも通り、何も証明しない。** 資格情報が無ければ付かないこと（陰性対照）と、
// AST レルムの `ServiceAuth` が同居していても漏れないこと（レルム取り違えの防止・IADR-0093 決定2）を対で置く。
public class LlmGatewayAuthWiringTests
{
    // MSP レルムの token エンドポイントが返す応答（実ネットワーク不使用）。
    private const string TokenJson = """{"access_token":"T","token_type":"Bearer","expires_in":300}""";

    // 名前付きクライアント "llm" で実際に 1 回送信し、ゲートウェイ側が観測したヘッダを返す。
    // （Program.cs が組んだハンドラの鎖をそのまま通す。一次ハンドラだけ差し替えている）
    private static async Task<RecordingHandler> SendAsync(Factory factory)
    {
        _ = factory.CreateClient(); // ホスト起動（Program.cs の配線がここで組み上がる）

        var http = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("llm");
        http.BaseAddress = new Uri("http://llm-gateway");
        using var response = await http.PostAsync(
            "/complete", new StringContent("""{"prompt":"p"}""", System.Text.Encoding.UTF8, "application/json"));

        return factory.Gateway;
    }

    // ---- 陽性: 資格情報があれば Bearer が付く（この試験が突然変異検査の受け皿である） ----------

    [Fact]
    public async Task LlmGatewayAuth設定時は_complete_送信に_MSPレルムの_Bearer_が付与される()
    {
        using var factory = new Factory(llmAuth: true);

        var gateway = await SendAsync(factory);

        gateway.LastAuthorization.Should().Be("Bearer T");
        // 付与が「たまたま残っていたヘッダ」ではなく、実際に token エンドポイントを叩いて得たものであること。
        factory.Token.CallCount.Should().Be(1);
    }

    // ---- 陰性対照 ---------------------------------------------------------------------------

    // 未設定なら何も付けない＝本変更前とバイト等価（基盤がまだ認可を掛けていない間も壊れない）。
    // これが無いと、上の陽性試験は「常に何か付く」実装でも緑になる。
    [Fact]
    public async Task 陰性対照_LlmGatewayAuth未設定なら_Authorization_を付けない()
    {
        using var factory = new Factory(llmAuth: false);

        var gateway = await SendAsync(factory);

        gateway.CallCount.Should().Be(1);
        gateway.LastAuthorization.Should().BeNull();
    }

    // 資格情報が半端（ClientSecret 欠如）なら無効＝安全側。「Authority だけ入れて動いたつもり」を捕まえる。
    [Fact]
    public async Task 陰性対照_ClientSecret欠如は無効でトークンを付けない()
    {
        using var factory = new Factory(llmAuth: false, llmAuthWithoutSecret: true);

        var gateway = await SendAsync(factory);

        gateway.LastAuthorization.Should().BeNull();
    }

    // 🔴 レルム取り違えの防止（IADR-0093 決定2 と同じ理由）。AST レルムの ServiceAuth が同一プロセスに
    // 居ても、`LlmGateway:Auth` が空なら "llm" には何も付かない。AST レルムのトークンを MSP へ出すと
    // issuer 不一致で 401 になり、**本 issue が直そうとしている故障を別経路で再生産する**。
    [Fact]
    public async Task 陰性対照_ASTレルムのServiceAuthが同居しても_llm_クライアントへ漏れない()
    {
        using var factory = new Factory(llmAuth: false, astServiceAuth: true);

        var gateway = await SendAsync(factory);

        gateway.CallCount.Should().Be(1);
        gateway.LastAuthorization.Should().BeNull();
    }

    // ---- fake ---------------------------------------------------------------------------------

    // 送信要求の Authorization を記録するだけのハンドラ。
    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastAuthorization { get; private set; }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class Factory(
        bool llmAuth,
        bool llmAuthWithoutSecret = false,
        bool astServiceAuth = false) : WebApplicationFactory<Program>
    {
        public RecordingHandler Gateway { get; } = new(HttpStatusCode.OK,
            """{"text":"{\"action\":\"Hold\"}","model":"claude-sonnet-5","inputTokens":1,"outputTokens":1,"sent":true}""");

        public RecordingHandler Token { get; } = new(HttpStatusCode.OK, TokenJson);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            // 🔴 `UseSetting`（ホスト構成）で与える。`ConfigureAppConfiguration` は **`builder.Build()` の時点**で
            // 適用されるため、`builder.Services` の登録中に構成を読む配線（本件のトークン付与・既存の
            // ServiceAuth / KnowledgeBase:Auth と同じ eager read）からは**まだ見えない**。
            // 実運用では構成は Program.cs の登録より前に揃っているため、この差はテストの与え方の問題である。
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");
            // 実 egress（HttpLlmCompletionClient）を選ばせる。未設定だとプレースホルダへ倒れる。
            builder.UseSetting("LlmGateway:BaseUrl", "http://llm-gateway");

            if (llmAuth || llmAuthWithoutSecret)
            {
                // MSP `platform` レルムの token エンドポイント（AST レルムではない）。
                builder.UseSetting($"{LlmGatewayAuth.SectionName}:TokenEndpoint",
                    "http://keycloak:8080/realms/platform/protocol/openid-connect/token");
                builder.UseSetting($"{LlmGatewayAuth.SectionName}:ClientId", "ai-stock-trading-kb-writer");
            }

            if (llmAuth)
                builder.UseSetting($"{LlmGatewayAuth.SectionName}:ClientSecret", "msp-realm-secret");

            if (astServiceAuth)
            {
                // 同一プロセスに同居する AST レルムの s2s（reports / risk / monitor が使う）。
                builder.UseSetting("Auth:Authority", "http://keycloak:8080/realms/ai-stock-trading");
                builder.UseSetting("ServiceAuth:ClientId", "ai-stock-trading-svc");
                builder.UseSetting("ServiceAuth:ClientSecret", "ast-realm-secret");
            }

            builder.ConfigureServices(services =>
            {
                // ADR-0013, IADR-0129, #354: 実 RabbitMQ を避けて Wolverine の外部トランスポートを無効化する。
                services.DisableAllExternalWolverineTransports();

                // 一番内側のハンドラだけ差し替える（トークン付与の DelegatingHandler は温存する）。
                services.AddHttpClient("llm").ConfigurePrimaryHttpMessageHandler(() => Gateway);
                services.AddHttpClient(LlmGatewayAuth.TokenClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => Token);
            });
        }
    }
}
