using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuditService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AuditService.Tests;

// FR-11, UC-07, IADR-0019: 監査照会エンドポイントの OwnerOnly 認可・時系列・limit を WebApplicationFactory で検証する。
public class AuditQueryEndpointsTests(AuditWorkerWebApplicationFactory factory)
    : IClassFixture<AuditWorkerWebApplicationFactory>
{
    private const string OwnerRole = "trading-owner";

    private static AuditEventRow Row(Guid correlationId, DateTimeOffset occurredAt, string type) => new()
    {
        Id = Guid.NewGuid(),
        EventType = type,
        CorrelationId = correlationId,
        Symbol = "AAPL",
        Summary = $"{type} 要約",
        Detail = "{}",
        OccurredAt = occurredAt,
        RecordedAt = DateTimeOffset.UtcNow,
    };

    private void Seed(params AuditEventRow[] rows)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        db.AuditEvents.AddRange(rows);
        db.SaveChanges();
    }

    private HttpClient OwnerClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);
        return client;
    }

    [Fact]
    public async Task 未認証は_401_ロール無しは_403()
    {
        var anon = factory.CreateClient();
        (await anon.GetAsync($"/audit/events/{Guid.NewGuid()}")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        var noRole = factory.CreateClient();
        noRole.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "some-other-role");
        (await noRole.GetAsync($"/audit/events/{Guid.NewGuid()}")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 相関IDで時系列昇順に注文の全記録を返す()
    {
        var corr = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);
        Seed(
            Row(corr, t0.AddMinutes(2), "OrderExecuted"),
            Row(corr, t0, "TradeDecisionMade"),
            Row(corr, t0.AddMinutes(1), "OrderApproved"));

        var res = await OwnerClient().GetAsync($"/audit/events/{corr}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var entries = await res.Content.ReadFromJsonAsync<List<AuditEntryDto>>();
        entries!.Select(e => e.EventType).Should()
            .ContainInOrder("TradeDecisionMade", "OrderApproved", "OrderExecuted");
    }

    [Fact]
    public async Task 直近照会は_limit_件を超えない()
    {
        var t0 = new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 4; i++)
            Seed(Row(Guid.NewGuid(), t0.AddMinutes(i), "OrderApproved"));

        var res = await OwnerClient().GetAsync("/audit/events?limit=2");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var entries = await res.Content.ReadFromJsonAsync<List<AuditEntryDto>>();
        entries!.Count.Should().Be(2);
    }

    [Fact]
    public async Task 直近照会の_limit_は下限_1_にクランプされる()
    {
        // limit=0（および負数）は Math.Clamp で 1 に補正され、エラーにならず最低 1 件返す。
        Seed(Row(Guid.NewGuid(), new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero), "OrderApproved"));

        var res = await OwnerClient().GetAsync("/audit/events?limit=0");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var entries = await res.Content.ReadFromJsonAsync<List<AuditEntryDto>>();
        entries!.Count.Should().Be(1);
    }

    // --- FR-06, #381, IADR-0199 決定2: 種別 × 期間の照会（日報の為替欄が引く） -------------------

    private const string ServiceRole = "trading-service";

    private HttpClient ServiceClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, ServiceRole);
        return client;
    }

    private static string ByType(DateTimeOffset from, DateTimeOffset to, string types) =>
        $"/audit/events/by-type?from={Uri.EscapeDataString(from.ToString("o"))}"
            + $"&to={Uri.EscapeDataString(to.ToString("o"))}&types={types}";

    // 🔴 **本 1 本だけ OwnerOrService へ開けた**（ReportService からの s2s）。
    // 未認証は 401 のままであることも同時に固定する（開けすぎていないこと）。
    [Fact]
    public async Task 種別期間照会は_サービスロールで引けるが_未認証は_401()
    {
        var t0 = new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero);
        var url = ByType(t0, t0.AddDays(1), "FxRateStale");

        (await factory.CreateClient().GetAsync(url)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        (await ServiceClient().GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 🔴 **否定形: 他の 2 本は OwnerOnly のままである**（必要な 1 本だけを開けた）。
    [Fact]
    public async Task 既存の照会はサービスロールでは引けない_開けたのは1本だけ()
    {
        (await ServiceClient().GetAsync("/audit/events")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "直近照会は OwnerOnly のままである");

        (await ServiceClient().GetAsync($"/audit/events/{Guid.NewGuid()}")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "相関照会も OwnerOnly のままである");
    }

    [Fact]
    public async Task 種別期間照会は_指定種別を期間で返す()
    {
        var t0 = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
        Seed(Row(Guid.NewGuid(), t0.AddHours(1), "FxRateStale"));
        Seed(Row(Guid.NewGuid(), t0.AddHours(2), "OrderApproved"));
        Seed(Row(Guid.NewGuid(), t0.AddDays(3), "FxRateStale"));

        var res = await ServiceClient().GetAsync(ByType(t0, t0.AddDays(1), "FxRateStale"));
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var entries = await res.Content.ReadFromJsonAsync<List<AuditEntryDto>>();
        entries.Should().ContainSingle();
        entries![0].EventType.Should().Be("FxRateStale");
    }

    // 🔴 **否定形: `types` の指定漏れを「全件」と読まない。** 全件取得に化けると
    // **取引履歴（機微情報）が意図せず流れる**。空・空白のみも同じ扱いである。
    [Theory]
    [InlineData("")]
    [InlineData(",,")]
    [InlineData("  ")]
    public async Task 種別の指定が空なら_400(string types)
    {
        var t0 = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);

        (await ServiceClient().GetAsync(ByType(t0, t0.AddDays(1), types))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    // **否定形**: 区間が逆・空なら 400（黙って空を返して「事象なし」に見せない）。
    [Fact]
    public async Task 区間が逆または空なら_400()
    {
        var t0 = new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

        (await ServiceClient().GetAsync(ByType(t0.AddDays(1), t0, "FxRateStale"))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await ServiceClient().GetAsync(ByType(t0, t0, "FxRateStale"))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "半開区間なので [t, t) は空区間である");
    }

    // 照会レスポンスの逆直列化用（AuditEntry の必要フィールドのみ）。
    private sealed record AuditEntryDto(string EventType, Guid CorrelationId, string? Symbol, DateTimeOffset OccurredAt);
}
