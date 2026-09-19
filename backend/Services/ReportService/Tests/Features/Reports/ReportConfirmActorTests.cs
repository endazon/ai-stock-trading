using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Wolverine.Tracking;
using Xunit;

namespace ReportService.Tests;

// FR-09, FR-07, UC-03, ADR-0003, IADR-0240 決定11, #774: 確定者（ReportConfirmed.Actor / AuthorizedBy）の解決を
// **発行されたイベント**で検証する。Discord Bot は owner マップ機密クライアント（client_credentials）のトークンで
// 確定を呼ぶため、トークンの主体は人ではない。Bot が本文で運ぶ「代理される利用者」（OnBehalfOf）を、
// **信頼するクライアントのトークンに限って**確定者として採る。
//
// 機密クライアントのトークンは TestAuthHandler の "X-Test-Azp"（azp）＋ "X-Test-Name"（NoName＝名前クレーム無し）で模す。
public class ReportConfirmActorTests
{
    private const string OwnerRole = "trading-owner";
    private const string OwnerClientId = "ai-stock-trading-owner";
    private const string PeriodKey = "daily-2026-09-10";

    private static object DraftBody() => new
    {
        Kind = "Daily",
        PeriodStart = "2026-09-10",
        BasedOn = (string?)null,
        AssumptionsVersion = 1,
        PolicySummary = "翌営業日は押し目買い",
        ExpectedVersion = 0,
    };

    private static WebApplicationFactory<Program> WithTrustedClients(
        ReportWorkerWebApplicationFactory baseFactory, string? trustedClientIds) =>
        baseFactory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Reports:DelegatedActor:TrustedClientIds"] = trustedClientIds,
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
    private static HttpClient ServiceAccountClient(WebApplicationFactory<Program> factory, string azp = OwnerClientId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);
        client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, TestAuthHandler.NoName);
        client.DefaultRequestHeaders.Add(TestAuthHandler.AzpHeader, azp);
        return client;
    }

    private static async Task<(HttpResponseMessage Response, ReportConfirmed[] Published)> ConfirmAsync(
        WebApplicationFactory<Program> factory, HttpClient client, object body)
    {
        (await UserClient(factory).PutAsJsonAsync($"/reports/{PeriodKey}", DraftBody()))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage response = null!;
        var session = await factory.Services.ExecuteAndWaitAsync(async () =>
        {
            response = await client.PostAsJsonAsync($"/reports/{PeriodKey}/confirm", body);
        });
        return (response, [.. session.Sent.MessagesOf<ReportConfirmed>()]);
    }

    [Fact]
    public async Task Bot経由の確定は_代理される利用者を確定者に_クライアントを認可の主体に残す()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithTrustedClients(baseFactory, OwnerClientId);

        var (response, published) = await ConfirmAsync(
            factory, ServiceAccountClient(factory), new { ExpectedVersion = 1, OnBehalfOf = "developer" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var e = published.Should().ContainSingle().Which;
        e.Actor.Should().Be("developer", "実際に操作した Discord 利用者（Keycloak 利用者名）が確定者である");
        e.Actor.Should().NotBe("unknown");
        e.AuthorizedBy.Should().Be(OwnerClientId, "認可の主体（owner マップ機密クライアント）も併せて残す");
    }

    // 🔴 なりすましの否定形: 利用者トークン直叩きでは本文の代理指定を**無視する**。確定は通るが確定者は本人。
    [Fact]
    public async Task 利用者トークンが_OnBehalfOf_を送っても無視され_確定者は本人のまま()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithTrustedClients(baseFactory, OwnerClientId);

        var (response, published) = await ConfirmAsync(
            factory, UserClient(factory, "owner"), new { ExpectedVersion = 1, OnBehalfOf = "someone-else" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var e = published.Should().ContainSingle().Which;
        e.Actor.Should().Be("owner");
        e.Actor.Should().NotBe("someone-else");
        e.AuthorizedBy.Should().BeNull("利用者本人のトークンでは代理は成立していない");
    }

    // 🔴 なりすましの否定形: 一覧に無い機密クライアントの代理指定は信じない。
    [Fact]
    public async Task 信頼一覧に無いクライアントの_OnBehalfOf_は無視され_確定者はクライアント主体になる()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithTrustedClients(baseFactory, OwnerClientId);

        var (response, published) = await ConfirmAsync(
            factory, ServiceAccountClient(factory, "some-other-client"),
            new { ExpectedVersion = 1, OnBehalfOf = "developer" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var e = published.Should().ContainSingle().Which;
        e.Actor.Should().Be("client:some-other-client");
        e.AuthorizedBy.Should().BeNull();
    }

    // fail-safe: 信頼一覧が未設定（既定）なら、owner クライアントの代理指定でも信じない（設定漏れで信頼を開かない）。
    [Fact]
    public async Task 信頼一覧が未設定なら_owner_クライアントの_OnBehalfOf_も無視する()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();

        var (response, published) = await ConfirmAsync(
            factory, ServiceAccountClient(factory), new { ExpectedVersion = 1, OnBehalfOf = "developer" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var e = published.Should().ContainSingle().Which;
        e.Actor.Should().Be($"client:{OwnerClientId}");
        e.AuthorizedBy.Should().BeNull();
    }

    // 操作者が取れないとき（旧版 Bot＝OnBehalfOf 無し・名前クレーム無し）は unknown ではなく、誰の資格で確定されたかを残す。
    [Fact]
    public async Task 操作者が取れない確定は_unknown_ではなく_client_azp_を確定者にする()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithTrustedClients(baseFactory, OwnerClientId);

        var (response, published) = await ConfirmAsync(
            factory, ServiceAccountClient(factory), new { ExpectedVersion = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var e = published.Should().ContainSingle().Which;
        e.Actor.Should().Be($"client:{OwnerClientId}");
        e.Actor.Should().NotBe("unknown");
        e.AuthorizedBy.Should().BeNull();
    }

    // 確定者を記録できない確定は行わない: 値域外の代理指定は信頼クライアントでも 400（確定もイベント発行もしない）。
    [Theory]
    [InlineData("developer\n")]
    [InlineData("dev eloper")]
    [InlineData("<@everyone>")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 値域外の_OnBehalfOf_は信頼クライアントでも_400_で確定しない(string onBehalfOf)
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithTrustedClients(baseFactory, OwnerClientId);

        var (response, published) = await ConfirmAsync(
            factory, ServiceAccountClient(factory), new { ExpectedVersion = 1, OnBehalfOf = onBehalfOf });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        published.Should().BeEmpty("確定していないのでイベントも出ない");

        // 報告書は Draft のまま（確定されていない）＝同じ版で改めて確定できる。
        var (retry, retried) = await ConfirmAgainAsync(factory, new { ExpectedVersion = 1, OnBehalfOf = "developer" });
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        retried.Should().ContainSingle().Which.Actor.Should().Be("developer");
    }

    private static async Task<(HttpResponseMessage Response, ReportConfirmed[] Published)> ConfirmAgainAsync(
        WebApplicationFactory<Program> factory, object body)
    {
        var client = ServiceAccountClient(factory);
        HttpResponseMessage response = null!;
        var session = await factory.Services.ExecuteAndWaitAsync(async () =>
        {
            response = await client.PostAsJsonAsync($"/reports/{PeriodKey}/confirm", body);
        });
        return (response, [.. session.Sent.MessagesOf<ReportConfirmed>()]);
    }
}
