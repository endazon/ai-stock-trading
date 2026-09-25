using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RiskManagementService.Features.RiskManagement;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, FR-11, FR-14, UC-06, ADR-0003, ADR-0041 決定 4, #871, IADR-0240 決定11, IADR-0383, IADR-0423:
// **乖離の取り込みの操作者**（台帳の Actor・PositionDriftAdopted の Actor / AuthorizedBy）の解決を、台帳
// （`GET /risk-controls/drift-adoptions`）・発行されたイベント・応答の 3 面で検証する。
//
// 取り込みの窓口は REST API と Discord Bot の両方である（ADR-0041 決定 4）。Bot は owner マップ機密クライアント
// （client_credentials）のトークンで呼ぶためトークンの主体は人ではなく、Bot が本文で運ぶ「代理される利用者」
// （onBehalfOf）を**信頼するクライアントのトークンに限って**操作者として採る。機密クライアントのトークンは
// TestAuthHandler の "X-Test-Azp"（azp）＋ "X-Test-Name"（NoName＝名前クレーム無し）で模す（段階遷移の T-129〜 と同じ）。
public class PositionDriftAdoptionDelegatedActorTests
{
    private const string OwnerRole = "trading-owner";
    private const string OwnerClientId = "ai-stock-trading-owner";
    private const string Path = "/risk-controls/position-drift/adopt";
    private const string Reason = "証券会社のアプリで全株を売却した";

    // 両方の窓口で同じ観測時刻を使う（記録の内容の比較のため）。
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-2);

    private static WebApplicationFactory<Program> WithTrustedClients(
        RiskWorkerWebApplicationFactory baseFactory, string? trustedClientIds) =>
        baseFactory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DelegatedActorOptions.TrustedClientIdsKey] = trustedClientIds,
            })));

    // 利用者トークン（名前つき・azp は SPA のクライアント）。
    private static HttpClient UserClient(WebApplicationFactory<Program> factory, string name)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);
        client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, name);
        client.DefaultRequestHeaders.Add(TestAuthHandler.AzpHeader, "ai-stock-trading-dev");
        return client;
    }

    // 機密クライアント（client_credentials）のトークン。**名前クレームを持たない**（Bot の実トークンの形）。
    private static HttpClient BotClient(WebApplicationFactory<Program> factory, string azp = OwnerClientId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);
        client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, TestAuthHandler.NoName);
        client.DefaultRequestHeaders.Add(TestAuthHandler.AzpHeader, azp);
        return client;
    }

    // 名前も azp も持たないトークン（操作者をまったく特定できない形）。
    private static HttpClient AnonymousOwnerClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);
        client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, TestAuthHandler.NoName);
        return client;
    }

    private static object Body(string? onBehalfOf) =>
        new { symbol = "AAPL", market = (int)Market.UnitedStates, reason = Reason, onBehalfOf };

    // 台帳へ建玉 100 株（前日に約定）を積み、全株が消えた観測を 2 回届けて乖離を報告済みにする（取り込める状態）。
    private static void SeedReportedDrift(WebApplicationFactory<Program> factory)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IPortfolioLedgerStore>();
            var decisionId = Guid.NewGuid();
            var at = DateTimeOffset.UtcNow.AddDays(-1);
            ledger.AppendApproval(
                decisionId,
                new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
                    100, 1m, PositionEffect.Open, StopLossPrice: 0.95m),
                at);
            ledger.AppendFill(decisionId, $"open-{decisionId:N}", 100, 1m, at);
        }

        for (var i = 0; i < 2; i++)
        {
            using var scope = factory.Services.CreateScope();
            var sp = scope.ServiceProvider;
            BrokerPositionSnapshot[] none = [];
            // 2 回目を最新の観測にする（両方の窓口で同じ ObservedAt になる）。
            sp.GetRequiredService<IBrokerPositionObservationStore>().Record(none, ObservedAt.AddSeconds(i - 1));
            var drifts = PositionDriftDetector.Detect(
                PortfolioProjection.ProjectOpenPositions(sp.GetRequiredService<IPortfolioLedgerStore>().GetFills()), none);
            sp.GetRequiredService<PositionDriftTracker>().ShouldReport(drifts);
        }
    }

    private static int LedgerQuantity(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        return PortfolioProjection
            .ProjectOpenPositions(scope.ServiceProvider.GetRequiredService<IPortfolioLedgerStore>().GetFills())
            .Where(p => p.Symbol == "AAPL")
            .Sum(p => p.Quantity);
    }

    private static async Task<(HttpResponseMessage Response, PositionDriftAdopted[] Published)> AdoptAsync(
        WebApplicationFactory<Program> factory, HttpClient client, object body)
    {
        HttpResponseMessage response = null!;
        var session = await factory.Services.ExecuteAndWaitForTestAsync(async () =>
        {
            response = await client.PostAsJsonAsync(Path, body);
        });
        return (response, [.. session.Sent.MessagesOf<PositionDriftAdopted>()]);
    }

    private static async Task<IReadOnlyList<DriftAdoptionDto>> LedgerRowsAsync(WebApplicationFactory<Program> factory)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return await UserClient(factory, "owner").GetFromJsonAsync<List<DriftAdoptionDto>>(
            $"/risk-controls/drift-adoptions?from={today.AddDays(-7):yyyy-MM-dd}&to={today.AddDays(7):yyyy-MM-dd}") ?? [];
    }

    // ---- T-10-983: Discord Bot 経由（信頼クライアント＋onBehalfOf）の取り込み ----

    // 🔴 T-10-983: 台帳・イベント・応答の操作者は**代理される利用者**、認可の主体はクライアント。理由文は送ったまま。
    [Fact]
    public async Task Bot経由の取り込みは代理される利用者を操作者に_クライアントを認可の主体に残す()
    {
        using var baseFactory = new RiskWorkerWebApplicationFactory();
        await using var factory = WithTrustedClients(baseFactory, OwnerClientId);
        SeedReportedDrift(factory);

        var (res, published) = await AdoptAsync(factory, BotClient(factory), Body(onBehalfOf: "developer"));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<AdoptionResponseDto>())!.Actor.Should().Be("developer");

        var e = published.Should().ContainSingle().Subject;
        e.Actor.Should().Be("developer");
        e.AuthorizedBy.Should().Be(OwnerClientId);
        // 🔴 理由文は窓口で加工しない（`actor=` の併記などをしない）。
        e.Reason.Should().Be(Reason);

        // 台帳（7 年保持・FR-11）: 操作者は利用者である（`unknown` でも client でもない）。
        (await LedgerRowsAsync(factory)).Should().ContainSingle().Which.Actor.Should().Be("developer");
        LedgerQuantity(factory).Should().Be(0);
    }

    // 🔴 T-10-983: **記録の内容は窓口に依らず同じ**（ADR-0041 決定 4）。同じ利用者が API から直接取り込んだ場合と、
    // Discord Bot から代理で取り込んだ場合とで、操作者・理由・取り込み前後の数量・観測値・観測時刻・実現損益の扱いが一致し、
    // 違いは認可の主体（AuthorizedBy）だけである。
    [Fact]
    public async Task API直接とBot経由とで記録の内容は認可の主体を除き同じ()
    {
        using var apiBase = new RiskWorkerWebApplicationFactory();
        await using var api = WithTrustedClients(apiBase, OwnerClientId);
        SeedReportedDrift(api);
        using var botBase = new RiskWorkerWebApplicationFactory();
        await using var bot = WithTrustedClients(botBase, OwnerClientId);
        SeedReportedDrift(bot);

        var (apiRes, apiEvents) = await AdoptAsync(api, UserClient(api, "developer"), Body(onBehalfOf: null));
        var (botRes, botEvents) = await AdoptAsync(bot, BotClient(bot), Body(onBehalfOf: "developer"));

        apiRes.StatusCode.Should().Be(HttpStatusCode.OK);
        botRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var viaApi = apiEvents.Should().ContainSingle().Subject;
        var viaBot = botEvents.Should().ContainSingle().Subject;

        Content(viaBot).Should().Be(Content(viaApi));
        viaApi.AuthorizedBy.Should().BeNull();
        viaBot.AuthorizedBy.Should().Be(OwnerClientId);

        var apiRow = (await LedgerRowsAsync(api)).Should().ContainSingle().Subject;
        var botRow = (await LedgerRowsAsync(bot)).Should().ContainSingle().Subject;
        (botRow with { AdoptionId = Guid.Empty, AdoptedAt = default })
            .Should().Be(apiRow with { AdoptionId = Guid.Empty, AdoptedAt = default });
    }

    // 窓口に依らず揃えるべき記録の内容（識別子と時刻・認可の主体を除く）。
    private static object Content(PositionDriftAdopted e) => new
    {
        e.Symbol,
        e.Market,
        e.LedgerQuantityBefore,
        e.LedgerQuantityAfter,
        e.BrokerQuantity,
        e.ObservedAt,
        e.CostBasisPrice,
        e.RealizedPnlRecorded,
        e.Actor,
        e.Reason,
    };

    // ---- T-10-984: 🔴 なりすまし・記録できない操作者の否定形 ----

    // 🔴 T-10-984: 利用者トークン直叩きで他人の名前を onBehalfOf に載せても採られない（操作者は本人・AuthorizedBy は null）。
    [Fact]
    public async Task 利用者トークン直叩きの_onBehalfOf_は無視され本人が操作者になる()
    {
        using var baseFactory = new RiskWorkerWebApplicationFactory();
        await using var factory = WithTrustedClients(baseFactory, OwnerClientId);
        SeedReportedDrift(factory);

        var (res, published) = await AdoptAsync(factory, UserClient(factory, "owner"), Body(onBehalfOf: "someone-else"));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var e = published.Should().ContainSingle().Subject;
        e.Actor.Should().Be("owner");
        e.AuthorizedBy.Should().BeNull();
        (await LedgerRowsAsync(factory)).Should().ContainSingle().Which.Actor.Should().Be("owner");
    }

    // 🔴 T-10-984: 信頼一覧が未設定（既定）なら Bot の onBehalfOf も信じない。人は分からないが「誰の資格で」は残る。
    [Fact]
    public async Task 信頼一覧が未設定なら_Bot_の_onBehalfOf_も信じず操作者はクライアント主体になる()
    {
        using var baseFactory = new RiskWorkerWebApplicationFactory();
        await using var factory = WithTrustedClients(baseFactory, trustedClientIds: null);
        SeedReportedDrift(factory);

        var (res, published) = await AdoptAsync(factory, BotClient(factory), Body(onBehalfOf: "developer"));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var e = published.Should().ContainSingle().Subject;
        e.Actor.Should().Be($"client:{OwnerClientId}");
        e.AuthorizedBy.Should().BeNull();
    }

    // 🔴 T-10-984: 信頼クライアントが値域外の onBehalfOf を送ったら 400。台帳は動かず、イベントも出ない。
    [Theory]
    [InlineData("dev owner")]
    [InlineData("山田")]
    [InlineData("owner\n")]
    public async Task 値域外の_onBehalfOf_は400で台帳は動かない(string onBehalfOf)
    {
        using var baseFactory = new RiskWorkerWebApplicationFactory();
        await using var factory = WithTrustedClients(baseFactory, OwnerClientId);
        SeedReportedDrift(factory);

        var (res, published) = await AdoptAsync(factory, BotClient(factory), Body(onBehalfOf));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("onBehalfOf");
        published.Should().BeEmpty();
        LedgerQuantity(factory).Should().Be(100);
        (await LedgerRowsAsync(factory)).Should().BeEmpty();
    }

    // 🔴 T-10-984: 操作者をまったく特定できない（名前も azp も無い）トークンは 400。`unknown` を台帳へ残さない。
    [Fact]
    public async Task 操作者を特定できないトークンは400で台帳は動かない()
    {
        using var baseFactory = new RiskWorkerWebApplicationFactory();
        await using var factory = WithTrustedClients(baseFactory, OwnerClientId);
        SeedReportedDrift(factory);

        var (res, published) = await AdoptAsync(factory, AnonymousOwnerClient(factory), Body(onBehalfOf: null));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("操作者を特定できない");
        published.Should().BeEmpty();
        LedgerQuantity(factory).Should().Be(100);
    }

    private sealed record AdoptionResponseDto(Guid AdoptionId, string Actor);

    // GET /risk-controls/drift-adoptions の応答（DriftAdoptionView と同形・比較に使う項目）。
    private sealed record DriftAdoptionDto(
        Guid AdoptionId,
        string Symbol,
        Market Market,
        TradeSide Side,
        int Quantity,
        int LedgerQuantityBefore,
        int BrokerQuantity,
        DateTimeOffset ObservedAt,
        string Actor,
        string Reason,
        DateTimeOffset AdoptedAt,
        TradeOrigin Origin,
        bool RealizedPnlRecorded);
}
