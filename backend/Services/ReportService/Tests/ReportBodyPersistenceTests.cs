using ReportService.Infrastructure.Persistence;
using ReportService.Features.Reports;
using ReportService.Domain;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ReportService.Tests;

// FR-06, IADR-0115, #280: 報告書本文（Markdown）の永続化を検証する。自動生成が保存した本文が利用者のレビュー対象になるため、
// ラウンドトリップと「手動 upsert で消えないこと」を固定する。EF とインメモリで同じ規則になっていることも併せて確認する。
public class ReportBodyPersistenceTests
{
    private const string Markdown = "---\nreport_type: daily\n---\n\n# 日報 2026-07-10\n";

    private static ReportDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<ReportDbContext>().UseInMemoryDatabase(dbName).Options);

    private static TradingReport Daily(string body = "", string policy = "方針") => new()
    {
        PeriodKey = "daily-2026-07-10",
        Kind = ReportKind.Daily,
        PeriodStart = new DateOnly(2026, 7, 10),
        PolicySummary = policy,
        AssumptionsVersion = 1,
        Body = body,
    };

    [Fact]
    public void 本文はラウンドトリップし別コンテキストからも読める()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
        {
            new EfReportStore(db).UpsertDraft(Daily(Markdown), 0);
        }

        using var db2 = NewContext(dbName);
        new EfReportStore(db2).Get("daily-2026-07-10")!.Report.Body.Should().Be(Markdown);
    }

    [Fact]
    public void 本文を持たない報告書は空文字として読み出される()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfReportStore(db);

        store.UpsertDraft(Daily(), 0);

        // 既存行（列追加前）と同じ NULL 経路。null ではなく空文字で返し、呼び出し側の分岐を増やさない。
        store.Get("daily-2026-07-10")!.Report.Body.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ef")]
    [InlineData("memory")]
    public void 本文を持たない改訂は生成済みの本文を消さない(string kind)
    {
        // 手動 upsert（PUT /reports/{periodKey}）は本文を受け取らないため Body は空で届く。
        // これで生成済み Markdown が消えると、利用者は方針を直した瞬間に報告書の中身を失う。
        IReportStore store = kind == "ef"
            ? new EfReportStore(NewContext(Guid.NewGuid().ToString()))
            : new InMemoryReportStore();

        var version = store.UpsertDraft(Daily(Markdown), 0);
        store.UpsertDraft(Daily(body: "", policy: "方針を書き直した"), version);

        var reloaded = store.Get("daily-2026-07-10")!.Report;
        reloaded.PolicySummary.Should().Be("方針を書き直した");
        reloaded.Body.Should().Be(Markdown);
    }

    [Theory]
    [InlineData("ef")]
    [InlineData("memory")]
    public void 非空の本文を渡した改訂は差し替える(string kind)
    {
        IReportStore store = kind == "ef"
            ? new EfReportStore(NewContext(Guid.NewGuid().ToString()))
            : new InMemoryReportStore();

        var version = store.UpsertDraft(Daily(Markdown), 0);
        store.UpsertDraft(Daily(body: "# 差し替えた本文\n"), version);

        store.Get("daily-2026-07-10")!.Report.Body.Should().Be("# 差し替えた本文\n");
    }
}
