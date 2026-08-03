using AiStockTrading.TestSupport.PlatformShim.Foundation.Middleware;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// IADR-0011, platform: 相関ID の引き継ぎ・生成・レスポンス反映を検証する。
public class CorrelationIdMiddlewareTests
{
    private const string Header = "X-Correlation-ID";

    private static async Task<HttpContext> Invoke(string? incoming)
    {
        var context = new DefaultHttpContext();
        if (incoming is not null)
        {
            context.Request.Headers[Header] = incoming;
        }

        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(context);
        return context;
    }

    [Fact]
    public async Task 受領した相関IDを引き継いでレスポンスへ反映する()
    {
        var context = await Invoke("abc-123");

        context.Response.Headers[Header].ToString().Should().Be("abc-123");
    }

    [Fact]
    public async Task 相関IDが無ければ生成してレスポンスへ反映する()
    {
        var context = await Invoke(incoming: null);

        var generated = context.Response.Headers[Header].ToString();
        generated.Should().NotBeNullOrWhiteSpace();
        Guid.TryParse(generated, out _).Should().BeTrue();
    }

    [Fact]
    public async Task 空の相関IDヘッダは生成値で置き換える()
    {
        var context = await Invoke("   ");

        var generated = context.Response.Headers[Header].ToString();
        Guid.TryParse(generated, out _).Should().BeTrue();
    }
}
