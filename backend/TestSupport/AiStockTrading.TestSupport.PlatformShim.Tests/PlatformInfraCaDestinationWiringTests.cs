using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Tls;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// 🔴 NFR, NFR-05, NFR-18, IADR-0639: 基盤の CA は**基盤の宛先だけ**が信頼する（システムの信頼ストアへは足さない）。
// 共通の配線が、CA の場所（PlatformInfraCa:CertificatePath）が設定されたときだけ、認可サーバーのメタデータの取得（JwtBearer の Backchannel）・
// トークンの取得の HttpClient・ブローカの amqps に基盤の CA の検証を付け、設定が無ければ何も変えないことを固定する。
// 外部 API の HttpClient に基盤の CA が付かないこと（陰性対照）は PlatformInfraCaTrustTests が実際の TLS で見る。
public class PlatformInfraCaDestinationWiringTests
{
    private const string CaPath = "/etc/platform-infra-ca/ca.crt";

    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    private static JwtBearerOptions JwtOptions(IConfiguration config)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(config);
        services.AddAiStockTradingAuth(config);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
    }

    private static HttpMessageHandler PrimaryHandlerOf(IServiceProvider provider, string clientName)
    {
        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName);
        while (handler is DelegatingHandler { InnerHandler: { } inner })
            handler = inner;
        return handler;
    }

    [Fact]
    public void 認可サーバーのメタデータの取得はCAの場所があるときだけ基盤のCAで検証する()
    {
        var withCa = JwtOptions(Config(
            ("Auth:Authority", "https://keycloak:8443/realms/platform"),
            (PlatformInfraCaTrust.PathKey, CaPath)));
        withCa.BackchannelHttpHandler.Should().NotBeNull();
        PlatformInfraCaTrust.IsPlatformInfraCaHandler(withCa.BackchannelHttpHandler!).Should().BeTrue();

        // 陰性対照: 場所が無ければ JwtBearer の既定のまま（ハンドラを足さない）。
        var withoutCa = JwtOptions(Config(("Auth:Authority", "http://keycloak:8080/realms/platform")));
        withoutCa.BackchannelHttpHandler.Should().BeNull();
    }

    [Fact]
    public void サービスのトークンの取得はCAの場所があるときだけ基盤のCAで検証する()
    {
        static ServiceProvider Build(string? caPath)
        {
            var config = Config(
                ("Auth:Authority", "https://keycloak:8443/realms/platform"),
                ("ServiceAuth:ClientId", "ai-stock-trading-svc-risk-management"),
                ("ServiceAuth:ClientSecret", "test-only"),
                (PlatformInfraCaTrust.PathKey, caPath));
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton(config);
            services.AddHttpClient("risk").AddAiStockTradingServiceToken(config);
            return services.BuildServiceProvider();
        }

        using var withCa = Build(CaPath);
        PlatformInfraCaTrust.IsPlatformInfraCaHandler(PrimaryHandlerOf(withCa, "ai-stock-trading-service-token")).Should().BeTrue();

        // 陰性対照: 場所が無ければトークンの取得の HttpClient も既定のまま。
        using var withoutCa = Build(null);
        PlatformInfraCaTrust.IsPlatformInfraCaHandler(PrimaryHandlerOf(withoutCa, "ai-stock-trading-service-token")).Should().BeFalse();
    }

    // 🔴 NFR, NFR-05, IADR-0646: サービスのトークンを載せる呼び出し先（取引ユニットのサービス）の HttpClient は、サービス間の TLS の段で
    // 受け側の証明書を基盤の CA で確かめる。CA の場所が無ければ既定のまま。トークンの資格が無い（付与が no-op）ときも同じに付く。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void サービスのトークンを載せる呼び出し先はCAの場所があるときだけ基盤のCAで検証する(bool withCredentials)
    {
        ServiceProvider Build(string? caPath)
        {
            var pairs = new List<(string, string?)>
            {
                ("Auth:Authority", "https://keycloak:8443/realms/platform"),
                (PlatformInfraCaTrust.PathKey, caPath),
            };
            if (withCredentials)
            {
                pairs.Add(("ServiceAuth:ClientId", "ai-stock-trading-svc-risk-management"));
                pairs.Add(("ServiceAuth:ClientSecret", "test-only"));
            }

            var config = Config([.. pairs]);
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton(config);
            services.AddHttpClient("risk").AddAiStockTradingServiceToken(config);
            // 陰性対照: サービスのトークンを載せない HttpClient（外部 API の形）には付かない。
            services.AddHttpClient("external");
            return services.BuildServiceProvider();
        }

        using var withCa = Build(CaPath);
        PlatformInfraCaTrust.IsPlatformInfraCaHandler(PrimaryHandlerOf(withCa, "risk")).Should().BeTrue();
        PlatformInfraCaTrust.IsPlatformInfraCaHandler(PrimaryHandlerOf(withCa, "external")).Should().BeFalse();

        using var withoutCa = Build(null);
        PlatformInfraCaTrust.IsPlatformInfraCaHandler(PrimaryHandlerOf(withoutCa, "risk")).Should().BeFalse();
    }

    [Fact]
    public void 基盤のレルムのトークンの取得はCAの場所があるときだけ基盤のCAで検証する()
    {
        static ServiceProvider Build(string? caPath)
        {
            var config = Config(
                ("LlmGateway:Auth:Authority", "https://keycloak:8443/realms/platform"),
                ("LlmGateway:Auth:ClientId", "ai-stock-trading-llm"),
                ("LlmGateway:Auth:ClientSecret", "test-only"),
                (PlatformInfraCaTrust.PathKey, caPath));
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton(config);
            services.AddHttpClient("llm").AddAiStockTradingPlatformRealmToken(config, "LlmGateway:Auth", "llm-token");
            return services.BuildServiceProvider();
        }

        using var withCa = Build(CaPath);
        PlatformInfraCaTrust.IsPlatformInfraCaHandler(PrimaryHandlerOf(withCa, "llm-token")).Should().BeTrue();
        using var withoutCa = Build(null);
        PlatformInfraCaTrust.IsPlatformInfraCaHandler(PrimaryHandlerOf(withoutCa, "llm-token")).Should().BeFalse();
    }

    [Fact]
    public void ブローカのamqpsはCAの場所があるときだけ基盤のCAで検証する()
    {
        var withCa = new SslOption { Enabled = true };
        WolverineExtensions.ApplyPlatformInfraCaTrust(withCa, CaPath);
        PlatformInfraCaTrust.IsPlatformInfraCaCallback(withCa.CertificateValidationCallback).Should().BeTrue();

        // 陰性対照: 場所が無ければ RabbitMQ.Client の既定の検証のまま（受け入れてよい誤りは共通の配線が空にしてある）。
        var withoutCa = new SslOption { Enabled = true };
        WolverineExtensions.ApplyPlatformInfraCaTrust(withoutCa, " ");
        withoutCa.CertificateValidationCallback.Should().BeNull();
    }
}
