using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// IADR-0011: Foundation の登録拡張が例外なくサービス登録できることのスモーク検証（実配線の E2E は #12 Slice B）。
public class FoundationRegistrationTests
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    [Fact]
    public void 可観測性の登録は例外なく解決できる()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddAiStockTradingObservability(EmptyConfig(), "risk-management-service");
        var provider = services.BuildServiceProvider();

        provider.GetService<TracerProvider>().Should().NotBeNull();
    }

    [Fact]
    public void Keycloak認証の登録は例外なく解決できる()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddAiStockTradingAuth(EmptyConfig());
        var provider = services.BuildServiceProvider();

        // 認証・認可・ロール展開の中核サービスが登録されていること。
        provider.GetService<Microsoft.AspNetCore.Authorization.IAuthorizationService>().Should().NotBeNull();
        provider.GetService<Microsoft.AspNetCore.Authentication.IClaimsTransformation>()
            .Should().BeOfType<KeycloakRolesClaimsTransformation>();
    }

    [Fact]
    public async Task OwnerOnly_認可ポリシーが登録される()
    {
        // FR-10/FR-19/FR-20, ADR-0007: 利用者のみポリシーが解決できること。
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingAuth(EmptyConfig());
        var provider = services.BuildServiceProvider();

        var policyProvider = provider
            .GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider>();
        var policy = await policyProvider.GetPolicyAsync(AiStockTradingAuthPolicies.OwnerOnly);

        policy.Should().NotBeNull();
    }

    [Fact]
    public void MassTransit共通再試行を適用したバスは解決できる()
    {
        // platform ADR-0003: UseAiStockTradingRetry を適用してもバス構成が成立すること。
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMassTransit(x =>
            x.UsingInMemory((_, cfg) => cfg.UseAiStockTradingRetry()));
        var provider = services.BuildServiceProvider();

        provider.GetService<IBusControl>().Should().NotBeNull();
    }
}
