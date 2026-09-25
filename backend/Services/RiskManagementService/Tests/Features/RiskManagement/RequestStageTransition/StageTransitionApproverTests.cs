using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Kernel.Trading;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Tests;

// FR-20, FR-11, FR-14, UC-06, ADR-0003, ADR-0008, #868, IADR-0062 決定3/決定4, IADR-0240 決定11, IADR-0383:
// **段階遷移の承認者（StageTransition.ApprovedBy / StageTransitioned.ApprovedBy・AuthorizedBy）の解決**を、
// 台帳（`GET /risk-controls/stage-gate/history`）と発行されたイベントの両方で検証する。
//
// Discord Bot は owner マップ機密クライアント（client_credentials）のトークンで承認を呼ぶため、トークンの主体は
// 人ではない。Bot が本文で運ぶ「代理される利用者」（onBehalfOf）を、**信頼するクライアントのトークンに限って**
// 承認者として採る。機密クライアントのトークンは TestAuthHandler の "X-Test-Azp"（azp）＋
// "X-Test-Name"（NoName＝名前クレーム無し）で模す。
//
// テスト ID: T-129〜T-137, T-140, T-141（`docs/tests/FR-20_staged-gates-tests.md`）。
public class StageTransitionApproverTests
{
    private const string OwnerRole = "trading-owner";
    private const string OwnerClientId = "ai-stock-trading-owner";
    private const string TransitionPath = "/risk-controls/stage-gate/transition";

    private static WebApplicationFactory<Program> WithTrustedClients(
        RiskWorkerWebApplicationFactory baseFactory, string? trustedClientIds) =>
        baseFactory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DelegatedActorOptions.TrustedClientIdsKey] = trustedClientIds,
            })));

    // 利用者トークン（名前つき・azp は SPA のクライアント）。
    private static HttpClient UserClient(WebApplicationFactory<Program> factory, string name = "owner")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);
        client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, name);
        client.DefaultRequestHeaders.Add(TestAuthHandler.AzpHeader, "ai-stock-trading-dev");
        return client;
    }

    // 機密クライアント（client_credentials）のトークン。**名前クレームを持たない**（稼働環境で unknown になった形）。
    private static HttpClient BotClient(WebApplicationFactory<Program> factory, string azp = OwnerClientId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);
        client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, TestAuthHandler.NoName);
        client.DefaultRequestHeaders.Add(TestAuthHandler.AzpHeader, azp);
        return client;
    }

    // 名前も azp も持たないトークン（承認者をまったく特定できない形）。
    private static HttpClient AnonymousOwnerClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);
        client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, TestAuthHandler.NoName);
        return client;
    }

    private static void SeedBacktestPassed(RiskWorkerWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IStagePerformanceStore>()
            .Save(new StagePerformance { BacktestPassed = true });
    }

    private static async Task<(HttpResponseMessage Response, StageTransitioned[] Published)> TransitionAsync(
        WebApplicationFactory<Program> factory, HttpClient client, object body)
    {
        HttpResponseMessage response = null!;
        var session = await factory.Services.ExecuteAndWaitForTestAsync(async () =>
        {
            response = await client.PostAsJsonAsync(TransitionPath, body);
        });
        return (response, [.. session.Sent.MessagesOf<StageTransitioned>()]);
    }

    private static async Task<IReadOnlyList<HistoryDto>> HistoryAsync(HttpClient client) =>
        await client.GetFromJsonAsync<List<HistoryDto>>("/risk-controls/stage-gate/history") ?? [];

    private sealed record HistoryDto(int Sequence, string ApprovedBy);

    // ---- T-129: Bot 経由の昇格で、台帳・イベントに正しい承認者が残る ----
    [Fact]
    public async Task T129_Bot経由の昇格は_代理される利用者を承認者に_クライアントを認可の主体に残す()
    {
        using var baseFactory = new RiskWorkerWebApplicationFactory();
        SeedBacktestPassed(baseFactory);
        await using var factory = WithTrustedClients(baseFactory, OwnerClientId);

        var (res, published) = await TransitionAsync(factory, BotClient(factory), new
        {
            targetStage = (int)TradingStage.Stage1Simulate,
            onBehalfOf = "developer",
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        // イベント: 操作した利用者と、認可の主体の両方。
        var e = published.Should().ContainSingle().Subject;
        e.ApprovedBy.Should().Be("developer");
        e.AuthorizedBy.Should().Be(OwnerClientId);

        // 台帳（7 年保持・FR-11）: 承認者は利用者である（`unknown` でも service-account でもない）。
        var history = await HistoryAsync(UserClient(factory));
        history.Should().ContainSingle().Which.ApprovedBy.Should().Be("developer");
    }

    // ---- T-130: 差し戻し（安全方向）でも承認者を残す ----
    [Fact]
    public async Task T130_Bot経由の差し戻しも_承認者と認可の主体を残す()
    {
        using var baseFactory = new RiskWorkerWebApplicationFactory();
        SeedBacktestPassed(baseFactory);
        await using var factory = WithTrustedClients(baseFactory, OwnerClientId);
        var bot = BotClient(factory);

        // Stage 1 へ上げてから差し戻す（降格方向は承認のみで受理される）。
        (await bot.PostAsJsonAsync(TransitionPath, new
        {
            targetStage = (int)TradingStage.Stage1Simulate,
            onBehalfOf = "developer",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var (res, published) = await TransitionAsync(factory, bot, new
        {
            targetStage = (int)TradingStage.Stage0Verification,
            onBehalfOf = "developer",
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var e = published.Should().ContainSingle().Subject;
        e.Kind.Should().Be(nameof(StageTransitionKind.Demotion));
        e.ApprovedBy.Should().Be("developer");
        e.AuthorizedBy.Should().Be(OwnerClientId);
    }

    // ---- T-131〜T-135: 🔴 なりすましの否定形（代理を信じない経路） ----
    //
    // いずれも「遷移は通るが、onBehalfOf は無視され、承認者はトークンの主体のまま」を固定する。
    // **本文の名前を承認者にできてしまうと、実資金ゲートの承認記録を他人の名前で作れる。**

    [Fact]
    public async Task T131_利用者トークン直叩きの_onBehalfOf_は無視される()
    {
        using var baseFactory = new RiskWorkerWebApplicationFactory();
        SeedBacktestPassed(baseFactory);
        await using var factory = WithTrustedClients(baseFactory, OwnerClientId);

        var (res, published) = await TransitionAsync(factory, UserClient(factory, "owner"), new
        {
            targetStage = (int)TradingStage.Stage1Simulate,
            onBehalfOf = "someone-else",
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var e = published.Should().ContainSingle().Subject;
        e.ApprovedBy.Should().Be("owner");
        e.AuthorizedBy.Should().BeNull();
    }

    [Fact]
    public async Task T132_一覧外のクライアントの_onBehalfOf_は無視され承認者はクライアント主体になる()
    {
        using var baseFactory = new RiskWorkerWebApplicationFactory();
        SeedBacktestPassed(baseFactory);
        await using var factory = WithTrustedClients(baseFactory, OwnerClientId);

        var (res, published) = await TransitionAsync(
            factory, BotClient(factory, azp: "some-other-client"), new
            {
                targetStage = (int)TradingStage.Stage1Simulate,
                onBehalfOf = "developer",
            });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var e = published.Should().ContainSingle().Subject;
        // 人は分からないが、誰の資格で承認されたかは残る（unknown へ倒す前に client:<azp>）。
        e.ApprovedBy.Should().Be("client:some-other-client");
        e.AuthorizedBy.Should().BeNull();
    }

    [Fact]
    public async Task T133_azp_が欠落したトークンでも名前があれば_onBehalfOf_は無視される()
    {
        using var baseFactory = new RiskWorkerWebApplicationFactory();
        SeedBacktestPassed(baseFactory);
        await using var factory = WithTrustedClients(baseFactory, OwnerClientId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);
        client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, "owner");

        var (res, published) = await TransitionAsync(factory, client, new
        {
            targetStage = (int)TradingStage.Stage1Simulate,
            onBehalfOf = "developer",
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var e = published.Should().ContainSingle().Subject;
        e.ApprovedBy.Should().Be("owner");
        e.AuthorizedBy.Should().BeNull();
    }

    [Fact]
    public async Task T134_azp_の大文字違いは信頼一覧に一致しない()
    {
        // 一致は Ordinal（大文字小文字を区別する）。Keycloak のクライアント ID は大文字小文字を区別する。
        using var baseFactory = new RiskWorkerWebApplicationFactory();
        SeedBacktestPassed(baseFactory);
        await using var factory = WithTrustedClients(baseFactory, OwnerClientId);

        var (res, published) = await TransitionAsync(
            factory, BotClient(factory, azp: "AI-Stock-Trading-Owner"), new
            {
                targetStage = (int)TradingStage.Stage1Simulate,
                onBehalfOf = "developer",
            });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var e = published.Should().ContainSingle().Subject;
        e.ApprovedBy.Should().Be("client:AI-Stock-Trading-Owner");
        e.AuthorizedBy.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task T135_信頼一覧が未設定なら誰の_onBehalfOf_も信じない(string? trusted)
    {
        using var baseFactory = new RiskWorkerWebApplicationFactory();
        SeedBacktestPassed(baseFactory);
        await using var factory = WithTrustedClients(baseFactory, trusted);

        var (res, published) = await TransitionAsync(factory, BotClient(factory), new
        {
            targetStage = (int)TradingStage.Stage1Simulate,
            onBehalfOf = "developer",
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var e = published.Should().ContainSingle().Subject;
        e.ApprovedBy.Should().Be($"client:{OwnerClientId}");
        e.AuthorizedBy.Should().BeNull();
    }

    // ---- T-136: 🔴 値域外の onBehalfOf は 400。台帳 0 行・イベント 0 通 ----
    [Theory]
    [InlineData("山田")]                                     // 非 ASCII（#861 の監査が実測）
    [InlineData("dev owner")]                                // 空白入り（同上）
    [InlineData("owner\n@everyone")]                         // 改行の注入
    [InlineData("owner\n")]                                  // 末尾 LF だけ（`$` なら LF の直前で一致してしまう）
    [InlineData("")]                                         // 空
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 65 文字
    public async Task T136_信頼クライアントの値域外の_onBehalfOf_は400で遷移しない(string onBehalfOf)
    {
        using var baseFactory = new RiskWorkerWebApplicationFactory();
        SeedBacktestPassed(baseFactory);
        await using var factory = WithTrustedClients(baseFactory, OwnerClientId);

        var (res, published) = await TransitionAsync(factory, BotClient(factory), new
        {
            targetStage = (int)TradingStage.Stage1Simulate,
            onBehalfOf,
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        published.Should().BeEmpty();
        (await HistoryAsync(UserClient(factory))).Should().BeEmpty();
    }

    [Theory]
    [InlineData("a")]                                                                    // 1 文字
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 64 文字
    [InlineData("first.last@example.com")]
    public async Task T136_値域の境界の内側は受理される(string onBehalfOf)
    {
        using var baseFactory = new RiskWorkerWebApplicationFactory();
        SeedBacktestPassed(baseFactory);
        await using var factory = WithTrustedClients(baseFactory, OwnerClientId);

        var (res, published) = await TransitionAsync(factory, BotClient(factory), new
        {
            targetStage = (int)TradingStage.Stage1Simulate,
            onBehalfOf,
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        published.Should().ContainSingle().Which.ApprovedBy.Should().Be(onBehalfOf);
    }

    // ---- T-137: 🔴 承認者を特定できない遷移は 400。`unknown` を実資金ゲートの台帳へ残さない ----
    [Fact]
    public async Task T137_名前も_azp_も無いトークンの遷移は400で台帳を1行も書かない()
    {
        using var baseFactory = new RiskWorkerWebApplicationFactory();
        SeedBacktestPassed(baseFactory);
        await using var factory = WithTrustedClients(baseFactory, OwnerClientId);

        var (res, published) = await TransitionAsync(factory, AnonymousOwnerClient(factory), new
        {
            targetStage = (int)TradingStage.Stage1Simulate,
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        published.Should().BeEmpty();
        (await HistoryAsync(UserClient(factory))).Should().BeEmpty();
    }

    [Fact]
    public async Task T137_承認者不明は空売り実弾解禁の_verdict_からも通らない()
    {
        // **相乗りの経路（IADR-0281 決定1）も同じ入口を通る。** 片方だけ塞ぐと迂回路になる。
        using var baseFactory = new RiskWorkerWebApplicationFactory();
        await using var factory = WithTrustedClients(baseFactory, OwnerClientId);

        var (res, published) = await TransitionAsync(factory, AnonymousOwnerClient(factory), new
        {
            approval = 1, // StageApprovalKind.ShortSellReleaseVerdict
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        published.Should().BeEmpty();
    }

    // ---- T-141: 空売り実弾解禁の verdict（相乗りの経路）も代理の承認者を台帳とイベントへ残す ----
    [Fact]
    public async Task T141_Bot経由の空売り実弾解禁_verdict_は_代理される利用者を承認者に_クライアントを認可の主体に残す()
    {
        // **相乗りの経路（IADR-0281 決定1）も同じ解決を通る。** verdict の分岐だけトークンの主体
        // （`ActorOf(http)` 相当）へ戻すと、Bot 経由の verdict は `client:<azp>` で台帳に残り、
        // 「誰が実弾解禁を確認したか」が失われる（T-137 の拒否だけでは検出できない）。
        using var baseFactory = new RiskWorkerWebApplicationFactory();
        await using var factory = WithTrustedClients(baseFactory, OwnerClientId);

        var (res, published) = await TransitionAsync(factory, BotClient(factory), new
        {
            approval = 1, // StageApprovalKind.ShortSellReleaseVerdict
            onBehalfOf = "developer",
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var e = published.Should().ContainSingle().Subject;
        e.Kind.Should().Be(nameof(StageTransitionKind.ShortSellReleaseVerdict));
        e.ApprovedBy.Should().Be("developer");
        e.AuthorizedBy.Should().Be(OwnerClientId);

        var history = await HistoryAsync(UserClient(factory));
        history.Should().ContainSingle().Which.ApprovedBy.Should().Be("developer");
    }

    // ---- 後方互換: onBehalfOf を添えない旧版 Bot／画面は従来どおり ----
    [Fact]
    public async Task onBehalfOf_を添えない要求は従来どおりトークンの主体が承認者になる()
    {
        using var baseFactory = new RiskWorkerWebApplicationFactory();
        SeedBacktestPassed(baseFactory);
        await using var factory = WithTrustedClients(baseFactory, OwnerClientId);

        var (res, published) = await TransitionAsync(factory, UserClient(factory, "owner"), new
        {
            targetStage = (int)TradingStage.Stage1Simulate,
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var e = published.Should().ContainSingle().Subject;
        e.ApprovedBy.Should().Be("owner");
        e.AuthorizedBy.Should().BeNull();
    }
}
