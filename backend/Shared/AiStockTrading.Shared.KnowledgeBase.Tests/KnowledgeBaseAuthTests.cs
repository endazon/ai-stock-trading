using System.Net;
using AiStockTrading.Shared.KnowledgeBase.Foundation.Extensions;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.Shared.KnowledgeBase.Tests;

// FR-08, IADR-0093: KB の s2s トークンは MSP レルムの専用クライアント（KnowledgeBase:Auth）で発行し、
// AST レルムの ServiceAuth（trading-service）とは分離した inline ハンドラで KB クライアントに閉じ込める。
public class KnowledgeBaseAuthTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    // --- token エンドポイント導出ゲート（KnowledgeBase:Auth のみを見る。AST Auth:Authority へはフォールバックしない） ---

    [Fact]
    public void TokenEndpoint明示があれば有効化しそれを用いる()
    {
        var options = KnowledgeBaseAuthExtensions.ReadOptions(Config(new()
        {
            ["KnowledgeBase:Auth:TokenEndpoint"] = "http://msp-kc/realms/microservices-platform/protocol/openid-connect/token",
            ["KnowledgeBase:Auth:ClientId"] = "ai-stock-trading-kb-writer",
            ["KnowledgeBase:Auth:ClientSecret"] = "kb-secret",
        }));

        options.IsEnabled.Should().BeTrue();
        options.TokenEndpoint.Should().Be("http://msp-kc/realms/microservices-platform/protocol/openid-connect/token");
    }

    [Fact]
    public void Authorityから_MSPレルムの_token_エンドポイントを導出する()
    {
        var options = KnowledgeBaseAuthExtensions.ReadOptions(Config(new()
        {
            ["KnowledgeBase:Auth:Authority"] = "http://keycloak:8080/realms/microservices-platform",
            ["KnowledgeBase:Auth:ClientId"] = "ai-stock-trading-kb-writer",
            ["KnowledgeBase:Auth:ClientSecret"] = "kb-secret",
        }));

        options.IsEnabled.Should().BeTrue();
        options.TokenEndpoint.Should().Be("http://keycloak:8080/realms/microservices-platform/protocol/openid-connect/token");
    }

    [Fact]
    public void AST_の_Auth_Authority_や_ServiceAuth_へはフォールバックしない()
    {
        // AST レルムの Authority と ServiceAuth が揃っていても、KnowledgeBase:Auth が空なら無効（＝トークンを付けない）。
        // これを取り違えると AST レルムのトークンで MSP を叩き 401 になる（#18 の故障）。
        var options = KnowledgeBaseAuthExtensions.ReadOptions(Config(new()
        {
            ["Auth:Authority"] = "http://keycloak:8080/realms/ai-stock-trading",
            ["ServiceAuth:ClientId"] = "ai-stock-trading-svc",
            ["ServiceAuth:ClientSecret"] = "svc-secret",
        }));

        options.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void ClientSecret欠如は無効()
    {
        var options = KnowledgeBaseAuthExtensions.ReadOptions(Config(new()
        {
            ["KnowledgeBase:Auth:Authority"] = "http://keycloak:8080/realms/microservices-platform",
            ["KnowledgeBase:Auth:ClientId"] = "ai-stock-trading-kb-writer",
        }));

        options.IsEnabled.Should().BeFalse();
    }

    // --- 統合: KB documents クライアントの POST /documents に Bearer が付与される ---

    [Fact]
    public async Task KnowledgeBaseAuth設定時は_documents_送信に_MSPレルムの_Bearer_が付与される()
    {
        var config = Config(new()
        {
            ["KnowledgeBase:Documents:BaseUrl"] = "http://documents",
            ["KnowledgeBase:Auth:TokenEndpoint"] = "http://msp-kc/token",
            ["KnowledgeBase:Auth:ClientId"] = "ai-stock-trading-kb-writer",
            ["KnowledgeBase:Auth:ClientSecret"] = "kb-secret",
        });

        var tokenStub = StubHttpMessageHandler.Json(
            HttpStatusCode.OK, """{"access_token":"T","token_type":"Bearer","expires_in":300}""");
        var docStub = StubHttpMessageHandler.Json(HttpStatusCode.Created, $$"""{"id":"{{Guid.NewGuid()}}"}""");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingKnowledgeBase(config);
        // token 取得クライアントと documents クライアントの一番内側のハンドラだけ差し替える（トークン付与の DelegatingHandler は温存）。
        services.AddHttpClient(KnowledgeBaseAuthExtensions.TokenClientName)
            .ConfigurePrimaryHttpMessageHandler(() => tokenStub);
        services.AddHttpClient(KnowledgeBaseExtensions.DocumentsClientName)
            .ConfigurePrimaryHttpMessageHandler(() => docStub);
        using var sp = services.BuildServiceProvider();

        var result = await sp.GetRequiredService<IKnowledgeBaseWriter>()
            .SaveAsync(new KnowledgeDocument("日報 2026-07-19"));

        result.Saved.Should().BeTrue();
        docStub.LastAuthorization.Should().Be("Bearer T");
    }

    // --- 分離（isolation）: KB:Auth 未設定なら、同一コンテナに AST レルム ServiceAuth があっても KB に漏れない ---

    [Fact]
    public async Task KnowledgeBaseAuth未設定なら_ASTレルムのServiceAuthがあっても_documents_にトークンを付けない()
    {
        var config = Config(new()
        {
            // 消費側 Worker が自分の s2s に用いる AST レルム ServiceAuth（KB とは無関係）。
            ["Auth:Authority"] = "http://keycloak:8080/realms/ai-stock-trading",
            ["ServiceAuth:ClientId"] = "ai-stock-trading-svc",
            ["ServiceAuth:ClientSecret"] = "svc-secret",
            // KB は保存先だけ設定・KnowledgeBase:Auth は空（無効）。
            ["KnowledgeBase:Documents:BaseUrl"] = "http://documents",
        });

        var docStub = StubHttpMessageHandler.Json(HttpStatusCode.Created, $$"""{"id":"{{Guid.NewGuid()}}"}""");

        var services = new ServiceCollection();
        services.AddLogging();
        // 消費側 Worker の AST レルム s2s クライアント（provider を TryAddSingleton 登録する）。
        services.AddHttpClient("worker-own-s2s").AddAiStockTradingServiceToken(config);
        services.AddAiStockTradingKnowledgeBase(config);
        services.AddHttpClient(KnowledgeBaseExtensions.DocumentsClientName)
            .ConfigurePrimaryHttpMessageHandler(() => docStub);
        using var sp = services.BuildServiceProvider();

        await sp.GetRequiredService<IKnowledgeBaseWriter>().SaveAsync(new KnowledgeDocument("t"));

        docStub.CallCount.Should().Be(1);
        docStub.LastAuthorization.Should().BeNull();
    }
}
