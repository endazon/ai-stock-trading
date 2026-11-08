using AiStockTrading.TestSupport.PlatformShim.Foundation.Tls;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NotificationService.Infrastructure.ExternalServices;
using Xunit;

namespace NotificationService.Tests;

// 🔴 NFR, NFR-05, IADR-0646: ボットが所有者のトークンを載せて呼ぶ取引ユニットのサービス（リスク管理・報告書・市場監視）の HttpClient は、
// サービス間の HTTP を TLS にした段で受け側の証明書を基盤の CA で確かめる（CA の場所が設定されているときだけ）。
// 外部（Discord の Webhook）の HttpClient には付けない（基盤の CA を外部の宛先で信頼しない。IADR-0639）。
public class ServiceTlsCaTrustWiringTests
{
    private static readonly string[] InternalClients =
    [
        "risk-kill-switch", "risk-pause", "risk-stage-gate", "risk-good-faith-violations", "risk-position-drift",
        "report-review", "report-confirm", "report-policy-revision", "report-regeneration", "market-monitor-watchlist",
    ];

    private static async Task<(bool[] Internal, bool External)> Probe(string? caPath)
    {
        await using var factory = new NotificationWorkerWebApplicationFactory().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PlatformInfraCaTrust.PathKey] = caPath,
            })));
        _ = factory.CreateClient();
        var handlers = factory.Services.GetRequiredService<IHttpMessageHandlerFactory>();
        return (
            InternalClients.Select(n => PlatformInfraCaTrust.IsPlatformInfraCaHandler(Primary(handlers.CreateHandler(n)))).ToArray(),
            PlatformInfraCaTrust.IsPlatformInfraCaHandler(Primary(handlers.CreateHandler(DiscordWebhookHttpClientExtensions.ClientName))));
    }

    [Fact]
    public async Task 所有者のトークンを載せる呼び出し先はCAの場所があるときだけ基盤のCAで検証し外部の宛先は検証しない()
    {
        var withCa = await Probe("/etc/platform-infra-ca/ca.crt");
        withCa.Internal.Should().AllSatisfy(x => x.Should().BeTrue());
        withCa.External.Should().BeFalse();

        // 陰性対照: CA の場所が無ければどれも既定のまま。
        var withoutCa = await Probe(null);
        withoutCa.Internal.Should().AllSatisfy(x => x.Should().BeFalse());
    }

    private static HttpMessageHandler Primary(HttpMessageHandler handler)
    {
        while (handler is DelegatingHandler d && d.InnerHandler is not null)
            handler = d.InnerHandler;
        return handler;
    }
}
