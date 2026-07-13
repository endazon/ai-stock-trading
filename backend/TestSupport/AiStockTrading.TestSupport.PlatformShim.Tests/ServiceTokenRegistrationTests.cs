using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// IADR-0051 決定 4: ServiceAuth の ClientId/Secret が揃った時のみトークン基盤を有効化する（fail-safe ゲート）。
// 未設定は no-op（ハンドラ・プロバイダを登録しない）＝現行挙動（401 → 安全既定）を厳密保持する。
public class ServiceTokenRegistrationTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ServiceProvider BuildWith(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient("risk").AddAiStockTradingServiceToken(Config(values));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ClientId_と_Secret_が揃えば_ClientCredentials_プロバイダを有効化する()
    {
        using var provider = BuildWith(new()
        {
            ["Auth:Authority"] = "http://keycloak/realms/ai-stock-trading",
            ["ServiceAuth:ClientId"] = "ai-stock-trading-svc",
            ["ServiceAuth:ClientSecret"] = "dev-secret",
        });

        provider.GetService<IServiceAccessTokenProvider>().Should().BeOfType<ClientCredentialsTokenProvider>();
    }

    [Fact]
    public void 明示の_TokenEndpoint_を優先する()
    {
        using var provider = BuildWith(new()
        {
            ["ServiceAuth:TokenEndpoint"] = "http://kc/token",
            ["ServiceAuth:ClientId"] = "svc",
            ["ServiceAuth:ClientSecret"] = "s",
        });

        provider.GetService<IServiceAccessTokenProvider>().Should().BeOfType<ClientCredentialsTokenProvider>();
    }

    [Fact]
    public void Secret_未設定は_no_op_プロバイダ未登録()
    {
        using var provider = BuildWith(new()
        {
            ["Auth:Authority"] = "http://keycloak/realms/ai-stock-trading",
            ["ServiceAuth:ClientId"] = "ai-stock-trading-svc",
        });

        provider.GetService<IServiceAccessTokenProvider>().Should().BeNull();
    }

    [Fact]
    public void 何も設定しなければ_no_op_プロバイダ未登録()
    {
        using var provider = BuildWith(new());

        provider.GetService<IServiceAccessTokenProvider>().Should().BeNull();
    }

    [Fact]
    public void TokenEndpoint_を導出できない_Authority_なし_は_no_op()
    {
        // ClientId/Secret はあるが Authority も TokenEndpoint も無い → 導出不能 → 無効（安全側）。
        using var provider = BuildWith(new()
        {
            ["ServiceAuth:ClientId"] = "svc",
            ["ServiceAuth:ClientSecret"] = "s",
        });

        provider.GetService<IServiceAccessTokenProvider>().Should().BeNull();
    }
}
