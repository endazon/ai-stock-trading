using System.Diagnostics;
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
        // #766: TracerProvider を内包する ServiceProvider を破棄しないと、グローバルな
        // ActivityListener（Microsoft.AspNetCore / System.Net.Http）がプロセス内に残り、
        // 同アセンブリの他クラス（例: CredentialBearingUriTraceRedactionTests の陰性対照）が
        // 干渉を受ける（PR #760 で実測）。
        using var provider = services.BuildServiceProvider();

        provider.GetService<TracerProvider>().Should().NotBeNull();
    }

    [Fact]
    public void Keycloak認証の登録は例外なく解決できる()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddAiStockTradingAuth(EmptyConfig());
        using var provider = services.BuildServiceProvider(); // #766: 同型の破棄漏れを避ける

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
        using var provider = services.BuildServiceProvider(); // #766: 同型の破棄漏れを避ける

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
        using var provider = services.BuildServiceProvider(); // #766: 同型の破棄漏れを避ける

        var policyProvider = provider
            .GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider>();
        var policy = await policyProvider.GetPolicyAsync(AiStockTradingAuthPolicies.OwnerOrService);

        policy.Should().NotBeNull();
    }

    // #766, #751, PR #760, IADR-0333: FoundationRegistrationTests が組み立てた TracerProvider を
    // 破棄せず、グローバルな ActivityListener がプロセス内に残っていた（PR #760 の陰性対照を実送信で
    // 書くと、単体では通るのにアセンブリ全体では落ちた。原因は本クラスの破棄漏れ）。
    //
    // 🔴 「他の [Fact] の後に実行される」ことを前提にしない。xUnit v3 の既定の TestCaseOrderer は
    // メソッド宣言順を保証しない（同一クラス内は直列に走るが、順序は無保証）。代わりに、
    // 上の 可観測性の登録は例外なく解決できる() と同じ配線（AddAiStockTradingObservability →
    // BuildServiceProvider → TracerProvider の解決）へ**専用の一意な ActivitySource**を相乗りさせ、
    // 破棄の前後で HasListeners() を同一テスト内で観測する（他クラスの ActivitySource 購読とは
    // 一意名のため衝突しない）。
    [Fact]
    public void 可観測性のTracerProviderを破棄するとActivityListenerが残らない()
    {
        using var marker = new ActivitySource($"ast.test.foundation-registration.{Guid.NewGuid():N}");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingObservability(EmptyConfig(), "foundation-registration-isolation");
        services.ConfigureOpenTelemetryTracerProvider(builder => builder.AddSource(marker.Name));

        var provider = services.BuildServiceProvider();
        try
        {
            provider.GetRequiredService<TracerProvider>();

            marker.HasListeners().Should().BeTrue(
                "TracerProvider を解決した直後は、相乗りさせた専用 ActivitySource にも " +
                "ActivityListener が付いているはずである（この前提が崩れていたら以降のアサーションは意味を持たない）");
        }
        finally
        {
            provider.Dispose(); // ← #766 で欠けていた破棄。ここを外すと下のアサーションが赤くなる。
        }

        marker.HasListeners().Should().BeFalse(
            "ServiceProvider（＝ TracerProvider）を破棄したら、グローバルな ActivityListener も " +
            "外れているべきである。外れていなければ、他クラスのテストがこの ActivityListener の " +
            "干渉を受け続ける（#751/PR #760 で実測）");
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
