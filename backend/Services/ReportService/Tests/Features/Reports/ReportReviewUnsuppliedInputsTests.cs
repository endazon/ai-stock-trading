using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Infrastructure.Persistence;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-07, FR-14, UC-03〜05, #840, IADR-0352 決定 5: 未供給だった入力の**記録**（ストア）と、
// レビュー照会（GET /reports/{periodKey}/review ＝ `/report show` と確認ボタンの前段が読む口）への**提示**を検証する。
public class ReportReviewUnsuppliedInputsTests
{
    private const string OwnerRole = "trading-owner";
    private const string Key = "daily-2026-07-10";

    private static TradingReport Degraded(params ReportInput[] unsupplied) => new()
    {
        PeriodKey = Key,
        Kind = ReportKind.Daily,
        PeriodStart = new DateOnly(2026, 7, 10),
        AssumptionsVersion = 1,
        PolicySummary = "方針",
        Body = "# 日報\n\n- **建玉を照会できませんでした**\n",
        UnsuppliedInputs = unsupplied,
    };

    private static ReportDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<ReportDbContext>().UseInMemoryDatabase(dbName).Options);

    // ---- レビュー照会 --------------------------------------------------------------------------

    [Fact]
    public async Task レビュー照会は_未供給だった入力を表示名で返す()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IReportStore>()
                .UpsertDraft(Degraded(ReportInput.OpenDUptime, ReportInput.OpenPositions, ReportInput.Narrative), 0);
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);

        var review = await client.GetFromJsonAsync<ReviewDto>($"/reports/{Key}/review");

        // 従来の 3 項目はそのまま（既存の読み手を壊さない）。
        review!.PeriodKey.Should().Be(Key);
        review.State.Should().Be("Drafting");
        review.Version.Should().Be(1);
        // 宣言順の表示名。
        review.UnsuppliedInputs.Should().Equal("建玉", "OpenD 稼働率", "散文（LLM）");
    }

    [Fact]
    public async Task 未供給が無い報告書のレビュー照会は_空の配列を返す()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IReportStore>().UpsertDraft(Degraded(), 0);
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);

        var review = await client.GetFromJsonAsync<ReviewDto>($"/reports/{Key}/review");

        // 🔴 否定形: 欠けていない報告書に警告の材料を返さない。
        review!.UnsuppliedInputs.Should().NotBeNull().And.BeEmpty();
    }

    // ---- ストア（EF・インメモリの両実装が同じ規則であること） ------------------------------------------

    public static TheoryData<string> Stores => ["ef", "in-memory"];

    private static IReportStore NewStore(string kind) => kind == "ef"
        ? new EfReportStore(NewContext(Guid.NewGuid().ToString()))
        : new InMemoryReportStore();

    [Fact]
    public void 未供給の記録は_別コンテキストから読んでも往復する()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
            new EfReportStore(db).UpsertDraft(Degraded(ReportInput.Narrative, ReportInput.Fills), 0);

        using var db2 = NewContext(dbName);
        new EfReportStore(db2).Get(Key)!.Report.UnsuppliedInputs
            .Should().Equal(ReportInput.Fills, ReportInput.Narrative);
        // 列には列挙名で入っている（表示名を変えても既存行が読める）。
        db2.Reports.Single().UnsuppliedInputs.Should().Be("Fills,Narrative");
    }

    [Fact]
    public void 既存行の_NULL_は_未供給なしとして読める()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
        {
            // 本変更前に作られた行（列が NULL）。
            db.Reports.Add(new ReportRow
            {
                PeriodKey = Key,
                Kind = ReportKind.Daily,
                PeriodStart = new DateOnly(2026, 7, 10),
                PolicySummary = "方針",
                Body = "本文",
                UnsuppliedInputs = null,
                Version = 1,
            });
            db.SaveChanges();
        }

        using var db2 = NewContext(dbName);
        new EfReportStore(db2).Get(Key)!.Report.UnsuppliedInputs.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void 本文を差し替えない改訂では_未供給の記録も残す(string kind)
    {
        // 手動 upsert（PUT /reports/{periodKey}）は本文を持たない。本文が残るのに警告だけ消える、を作らない。
        var store = NewStore(kind);
        store.UpsertDraft(Degraded(ReportInput.OpenPositions), 0);

        store.UpsertDraft(Degraded() with { Body = string.Empty, PolicySummary = "改訂した方針" }, 1);

        var report = store.Get(Key)!.Report;
        report.PolicySummary.Should().Be("改訂した方針");
        report.Body.Should().Contain("建玉を照会できませんでした");
        report.UnsuppliedInputs.Should().Equal(ReportInput.OpenPositions);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void 確定しても_未供給の記録は残る(string kind)
    {
        // 確定済みの報告書を後から見たときにも「何が欠けたまま確定されたか」が分かる。
        var store = NewStore(kind);
        store.UpsertDraft(Degraded(ReportInput.OpenPositions), 0);

        store.Confirm(Key, 1, DateTimeOffset.UtcNow)!.Report.UnsuppliedInputs.Should().Equal(ReportInput.OpenPositions);
        store.Get(Key)!.Report.UnsuppliedInputs.Should().Equal(ReportInput.OpenPositions);
    }

    private sealed record ReviewDto(string PeriodKey, string State, int Version, IReadOnlyList<string>? UnsuppliedInputs);
}
