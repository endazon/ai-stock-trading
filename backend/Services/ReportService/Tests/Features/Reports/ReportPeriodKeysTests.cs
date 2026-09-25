using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Infrastructure.Persistence;
using Xunit;

namespace ReportService.Tests;

// FR-14, FR-07, UC-03〜05, #843 項目1, IADR-0418: 入力補完の候補用の軽い一覧（GET /reports/period-keys）。
// Discord の `/report` の補完は打鍵ごとに一覧を読むため、本文を含む `GET /reports` ではなく会話キーと開始日だけを返す。
public class ReportPeriodKeysTests
{
    private const string OwnerRole = "trading-owner";
    private const string ServiceRole = "trading-service";

    private static HttpClient ClientWithRoles(ReportWorkerWebApplicationFactory factory, string roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    private static object DraftBody(string kind, string periodStart) => new
    {
        Kind = kind,
        PeriodStart = periodStart,
        BasedOn = (string?)null,
        AssumptionsVersion = 1,
        PolicySummary = "翌営業日は押し目買い",
        ExpectedVersion = 0,
    };

    private static TradingReport Report(string periodKey, DateOnly periodStart, string body = "# 本文\n") => new()
    {
        PeriodKey = periodKey,
        Kind = ReportKind.Daily,
        PeriodStart = periodStart,
        PolicySummary = "方針",
        AssumptionsVersion = 1,
        Body = body,
    };

    [Fact]
    public async Task 会話キーと開始日だけを新しい順に返し本文も要約も状態も含まない()
    {
        // 受け入れ基準 1・3: 射影は会話キーと開始日だけ（IADR-0240 決定4/5 と同じ）。リテラルのルートは
        // /{periodKey} より優先され、既存の 1 件照会も壊さない。
        await using var factory = new ReportWorkerWebApplicationFactory();
        var owner = ClientWithRoles(factory, OwnerRole);
        (await owner.PutAsJsonAsync("/reports/daily-2026-09-17", DraftBody("Daily", "2026-09-17")))
            .IsSuccessStatusCode.Should().BeTrue();
        (await owner.PutAsJsonAsync("/reports/weekly-2026-W38", DraftBody("Weekly", "2026-09-14")))
            .IsSuccessStatusCode.Should().BeTrue();
        (await owner.PutAsJsonAsync("/reports/daily-2026-09-18", DraftBody("Daily", "2026-09-18")))
            .IsSuccessStatusCode.Should().BeTrue();

        using var response = await owner.GetAsync("/reports/period-keys");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = json.RootElement.EnumerateArray().ToList();
        rows.Select(r => r.GetProperty("periodKey").GetString())
            .Should().Equal("daily-2026-09-18", "daily-2026-09-17", "weekly-2026-W38");
        rows[0].GetProperty("periodStart").GetString().Should().Be("2026-09-18");
        rows.Should().AllSatisfy(r => r.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("periodKey", "periodStart"));

        (await owner.GetAsync("/reports/daily-2026-09-18")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 報告書が無ければ空の配列を返す()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();

        var rows = await ClientWithRoles(factory, OwnerRole)
            .GetFromJsonAsync<List<JsonElement>>("/reports/period-keys");

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task 利用者のみ_未認証は_401_サービスは_403()
    {
        // 受け入れ基準 2: 一覧（GET /reports）と同じ OwnerOnly。会話キーの存在も漏らさない。
        await using var factory = new ReportWorkerWebApplicationFactory();

        (await factory.CreateClient().GetAsync("/reports/period-keys"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ClientWithRoles(factory, ServiceRole).GetAsync("/reports/period-keys"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("ef")]
    [InlineData("memory")]
    public void ストアの射影は開始日の降順_同日は会話キーの降順(string kind)
    {
        // 受け入れ基準 4: EF とインメモリで同じ並び。EF は別コンテキストから読む（射影が永続化越しに成り立つ）。
        var dbName = Guid.NewGuid().ToString();
        IReportStore store = kind == "ef" ? new EfReportStore(NewContext(dbName)) : new InMemoryReportStore();
        store.UpsertDraft(Report("daily-2026-09-17", new DateOnly(2026, 9, 17)), 0);
        store.UpsertDraft(Report("monthly-2026-09", new DateOnly(2026, 9, 1)), 0);
        store.UpsertDraft(Report("weekly-2026-W36", new DateOnly(2026, 9, 1)), 0);
        store.UpsertDraft(Report("daily-2026-09-18", new DateOnly(2026, 9, 18)), 0);

        var reader = kind == "ef" ? new EfReportStore(NewContext(dbName)) : store;

        reader.ListPeriodKeys().Should().Equal(
            new ReportPeriodKeyItem("daily-2026-09-18", new DateOnly(2026, 9, 18)),
            new ReportPeriodKeyItem("daily-2026-09-17", new DateOnly(2026, 9, 17)),
            new ReportPeriodKeyItem("weekly-2026-W36", new DateOnly(2026, 9, 1)),
            new ReportPeriodKeyItem("monthly-2026-09", new DateOnly(2026, 9, 1)));
    }

    private static ReportDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<ReportDbContext>().UseInMemoryDatabase(dbName).Options);
}
