using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
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
        // FR-10/FR-19/FR-20, ADR-0003/ADR-0007/ADR-0008: 利用者のみポリシーが解決できること。
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
    public async Task OwnerOrService_認可ポリシーが登録される()
    {
        // IADR-0051: 読み取り系同期照会は利用者またはサービスが呼べるポリシーが解決できること。
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingAuth(EmptyConfig());
        var provider = services.BuildServiceProvider();

        var policyProvider = provider
            .GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider>();
        var policy = await policyProvider.GetPolicyAsync(AiStockTradingAuthPolicies.OwnerOrService);

        policy.Should().NotBeNull();
    }

    [Fact]
    public async Task 共通再試行を適用したメッセージ基盤は解決できる()
    {
        // NFR, ADR-0013, IADR-0129 決定 5, #354: UseAiStockTradingRabbitMq が適用する共通の再試行方針
        // （2s/10s/30s の 3 回 → <queue>_error へ退避）を含む配線でホストが成立すること。
        //
        // 本テストは MassTransit 時代の `MassTransit共通再試行を適用したバスは解決できる`
        // （`UseAiStockTradingRetry` ＋ `IBusControl` の解決）の後継である。#354 第 3 段階で
        // `MassTransitExtensions` を撤去したため、同じ表明（共通再試行を適用した構成が成立する）を
        // Wolverine の共通配線に対して行う。
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAiStockTradingRabbitMq(
                    "ai-stock-trading.foundation-smoke", "amqp://guest:guest@localhost:5672");
                // 実ブローカへ接続しない（ローカル・CI ともに RabbitMQ を要求しない）。
                opts.StubAllExternalTransports();
            })
            .StartAsync();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        // 再試行の段取り（間隔 2s/10s/30s → 使い切ったら error キューへ退避）を**実行結果の記述で**固定する。
        // 間隔や退避先が黙って変われば、継続失敗が DLQ へ落ちるまでの時間と運用手順（<queue>_error を覗く）が
        // 変わってしまう。Wolverine が自ら組み立てた規則の記述を読むため、思い込みではなく実測で固定される。
        ((IWithFailurePolicies)runtime.Options).Failures.Select(rule => rule.ToString()).Should().Contain(
            "On All exceptions \u2014 attempt 1: Retry inline with a delay of 00:00:02;"
            + " attempt 2: Retry inline with a delay of 00:00:10;"
            + " attempt 3: Retry inline with a delay of 00:00:30;"
            + " attempt 4: Move to error queue",
            "IADR-0129 決定 5 の再試行・DLQ 方針（MassTransit 時代の UseAiStockTradingRetry と同値）");

        await host.StopAsync();
    }
}
