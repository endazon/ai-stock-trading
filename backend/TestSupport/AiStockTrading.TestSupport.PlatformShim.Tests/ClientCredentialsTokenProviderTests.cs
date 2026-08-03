using System.Net;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// IADR-0051 決定 1/4/5: client_credentials によるサービストークン取得・キャッシュ・フェイルセーフ（null）・可観測性を
// fake HttpMessageHandler で検証する（実 Keycloak 不使用）。取得失敗はワーカーを止めず null に倒す。
public class ClientCredentialsTokenProviderTests
{
    private static ServiceAuthOptions Options() => new()
    {
        TokenEndpoint = "http://keycloak/realms/ai-stock-trading/protocol/openid-connect/token",
        ClientId = "ai-stock-trading-svc",
        ClientSecret = "dev-secret",
        Scope = null,
        RefreshSkewSeconds = 30,
    };

    private static ClientCredentialsTokenProvider Provider(
        HttpMessageHandler handler, MutableTimeProvider? time = null, ServiceAuthOptions? options = null, RecordingLogger<ClientCredentialsTokenProvider>? logger = null) =>
        new(new HttpClient(handler), options ?? Options(), logger ?? new RecordingLogger<ClientCredentialsTokenProvider>(), time ?? new MutableTimeProvider());

    [Fact]
    public async Task 成功時は_access_token_を返し_token_エンドポイントへ_client_credentials_を送る()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"access_token":"abc","expires_in":300,"token_type":"Bearer"}""");

        var token = await Provider(handler).GetTokenAsync();

        token.Should().Be("abc");
        handler.LastUri.Should().Be("http://keycloak/realms/ai-stock-trading/protocol/openid-connect/token");
        handler.LastBody.Should().Contain("grant_type=client_credentials")
            .And.Contain("client_id=ai-stock-trading-svc")
            .And.Contain("client_secret=dev-secret");
    }

    [Fact]
    public async Task 有効期限内はキャッシュし_token_エンドポイントを再呼び出ししない()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"access_token":"abc","expires_in":300}""");
        var provider = Provider(handler);

        (await provider.GetTokenAsync()).Should().Be("abc");
        (await provider.GetTokenAsync()).Should().Be("abc");

        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task 期限切れ後は再取得する()
    {
        var time = new MutableTimeProvider();
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"access_token":"t1","expires_in":300}""");
        var provider = Provider(handler, time);

        (await provider.GetTokenAsync()).Should().Be("t1");

        // expires_in 300 − skew 30 = 270 秒有効。271 秒進めると失効し再取得する。
        handler.Body = """{"access_token":"t2","expires_in":300}""";
        time.Advance(TimeSpan.FromSeconds(271));

        (await provider.GetTokenAsync()).Should().Be("t2");
        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task 非_2xx_は_null_を返し警告し_キャッシュしない()
    {
        var logger = new RecordingLogger<ClientCredentialsTokenProvider>();
        var handler = new CapturingHandler(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""");
        var provider = Provider(handler, logger: logger);

        (await provider.GetTokenAsync()).Should().BeNull();
        (await provider.GetTokenAsync()).Should().BeNull();

        handler.CallCount.Should().Be(2); // キャッシュしないため毎回試行する
        logger.Warnings.Should().NotBeEmpty();
    }

    [Fact]
    public async Task 例外_不達_は_null_を返し警告する()
    {
        var logger = new RecordingLogger<ClientCredentialsTokenProvider>();

        (await Provider(new ThrowingHandler(), logger: logger).GetTokenAsync()).Should().BeNull();

        logger.Warnings.Should().NotBeEmpty();
    }

    [Fact]
    public async Task タイムアウトは_null_を返す()
    {
        var http = new HttpClient(new DelayingHandler(TimeSpan.FromSeconds(2))) { Timeout = TimeSpan.FromMilliseconds(50) };
        var provider = new ClientCredentialsTokenProvider(
            http, Options(), new RecordingLogger<ClientCredentialsTokenProvider>(), new MutableTimeProvider());

        (await provider.GetTokenAsync()).Should().BeNull();
    }

    [Fact]
    public async Task access_token_欠落の_200_は_null_を返す()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"token_type":"Bearer","expires_in":300}""");

        (await Provider(handler).GetTokenAsync()).Should().BeNull();
    }

    // --- fakes ---

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string Body { get; set; } = body;
        public string? LastUri { get; private set; }
        public string? LastBody { get; private set; }
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastUri = request.RequestUri?.ToString();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(Body) };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Keycloak 不達");
    }

    private sealed class DelayingHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
