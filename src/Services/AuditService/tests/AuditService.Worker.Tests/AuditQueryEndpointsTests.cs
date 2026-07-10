using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AiStockTrading.Audit.Worker.Foundation.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.Audit.Worker.Tests;

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

    // 照会レスポンスの逆直列化用（AuditEntry の必要フィールドのみ）。
    private sealed record AuditEntryDto(string EventType, Guid CorrelationId, string? Symbol, DateTimeOffset OccurredAt);
}
