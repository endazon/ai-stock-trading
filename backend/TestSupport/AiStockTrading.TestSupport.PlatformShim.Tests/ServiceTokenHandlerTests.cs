using System.Net;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// IADR-0051 決定 2: 発信要求へサービストークンを Bearer 付与する DelegatingHandler の検証。
// トークンが得られない場合はヘッダを付けずに送信し（→ 401 → 呼び出し側フェイルセーフ）、例外は送出しない。
public class ServiceTokenHandlerTests
{
    private static async Task<HttpRequestMessage> SendThroughAsync(IServiceAccessTokenProvider provider)
    {
        var inner = new CapturingHandler();
        var handler = new ServiceTokenHandler(provider) { InnerHandler = inner };
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://risk") };

        await client.GetAsync("/risk-controls/open-positions");
        return inner.LastRequest!;
    }

    [Fact]
    public async Task トークンがあれば_Authorization_Bearer_を付与する()
    {
        var request = await SendThroughAsync(new StubTokenProvider("abc"));

        request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        request.Headers.Authorization!.Parameter.Should().Be("abc");
    }

    [Fact]
    public async Task トークンが_null_ならヘッダを付けない()
    {
        var request = await SendThroughAsync(new StubTokenProvider(null));

        request.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task 既存の_Authorization_を上書きしない()
    {
        var inner = new CapturingHandler();
        var handler = new ServiceTokenHandler(new StubTokenProvider("service-token")) { InnerHandler = inner };
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://risk") };

        using var req = new HttpRequestMessage(HttpMethod.Get, "/x");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "user-token");
        await client.SendAsync(req);

        inner.LastRequest!.Headers.Authorization!.Parameter.Should().Be("user-token");
    }

    private sealed class StubTokenProvider(string? token) : IServiceAccessTokenProvider
    {
        public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(token);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
