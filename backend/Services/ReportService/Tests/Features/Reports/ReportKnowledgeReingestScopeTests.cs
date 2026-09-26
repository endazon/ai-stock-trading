using System.Net;
using AwesomeAssertions;
using ReportService.Domain;
using ReportService.Features.Reports.ReingestKnowledgeBase;
using Xunit;
using static ReportService.Tests.ReportKnowledgeReingestTestKit;

namespace ReportService.Tests;

// FR-08, #1028, IADR-0436 決定 1: 入れ直しの対象範囲（T-10-1499）。期間キーは文字列では比べず期間へ直す。
public class ReportKnowledgeReingestScopeTests
{
    private static TradingReport Report(ReportKind kind, DateOnly start) =>
        new() { PeriodKey = ReportPeriod.ExpectedKey(kind, start), Kind = kind, PeriodStart = start };

    // T-10-1499: 期間キー → 期間（初日・末日）。ReportPeriod.ExpectedKey の逆になっている。
    [Theory]
    [InlineData("daily-2026-07-10", "2026-07-10", "2026-07-10")]
    [InlineData("weekly-2026-W28", "2026-07-06", "2026-07-12")]
    [InlineData("weekly-2026-W01", "2025-12-29", "2026-01-04")]
    [InlineData("weekly-2026-W53", "2026-12-28", "2027-01-03")]
    [InlineData("monthly-2026-02", "2026-02-01", "2026-02-28")]
    [InlineData("monthly-2028-02", "2028-02-01", "2028-02-29")]
    public void 期間キーを期間へ直す(string key, string start, string end)
    {
        ReportKnowledgeReingestScope.TryParsePeriod(key, out var s, out var e).Should().BeTrue();
        s.Should().Be(DateOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture));
        e.Should().Be(DateOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void 期間キーの往復はReportPeriodと一致する()
    {
        for (var day = new DateOnly(2025, 12, 20); day <= new DateOnly(2027, 1, 10); day = day.AddDays(1))
        {
            foreach (var kind in Enum.GetValues<ReportKind>())
            {
                ReportKnowledgeReingestScope.TryParsePeriod(ReportPeriod.ExpectedKey(kind, day), out var s, out var e)
                    .Should().BeTrue();
                day.Should().BeOnOrAfter(s).And.BeOnOrBefore(e);
            }
        }
    }

    [Theory]
    [InlineData("daily-2026-13-01")]
    [InlineData("daily-2026-7-1")]
    [InlineData("weekly-2025-W53")]
    [InlineData("weekly-2026-W00")]
    [InlineData("weekly-2026-28")]
    [InlineData("monthly-2026-13")]
    [InlineData("quarterly-2026-Q1")]
    // PR #1038 の監査 3: 9999 年は期間の末日の計算が DateOnly の範囲を超える（旧実装は例外で 500）。
    [InlineData("monthly-9999-12")]
    [InlineData("weekly-9999-W52")]
    [InlineData("daily-9999-12-31")]
    [InlineData("Daily-2026-07-10")]
    [InlineData("daily-2026-07-10\n")]
    [InlineData("")]
    public void 形の違う期間キーは受けない(string key) =>
        ReportKnowledgeReingestScope.TryParsePeriod(key, out _, out _).Should().BeFalse();

    // T-10-1499: 範囲は from の初日 ≦ PeriodStart ≦ to の末日（種別を問わない）。片側だけも可。
    [Fact]
    public void 範囲は種別を問わず期間で絞る()
    {
        var (scope, error) = ReportKnowledgeReingestScope.Parse(null, "weekly-2026-W28", "monthly-2026-08");
        error.Should().BeNull();

        scope!.Includes(Report(ReportKind.Daily, new DateOnly(2026, 7, 5))).Should().BeFalse("W28 は 07-06 から");
        scope.Includes(Report(ReportKind.Weekly, new DateOnly(2026, 7, 6))).Should().BeTrue();
        scope.Includes(Report(ReportKind.Daily, new DateOnly(2026, 8, 31))).Should().BeTrue("to の月の末日まで含む");
        scope.Includes(Report(ReportKind.Monthly, new DateOnly(2026, 8, 1))).Should().BeTrue();
        scope.Includes(Report(ReportKind.Daily, new DateOnly(2026, 9, 1))).Should().BeFalse();
        scope.Describe().Should().Be("weekly-2026-W28..monthly-2026-08");

        var (fromOnly, _) = ReportKnowledgeReingestScope.Parse(null, "daily-2026-07-10", null);
        fromOnly!.Includes(Report(ReportKind.Daily, new DateOnly(2030, 1, 1))).Should().BeTrue();
        fromOnly.Includes(Report(ReportKind.Daily, new DateOnly(2026, 7, 9))).Should().BeFalse();
        fromOnly.Describe().Should().Be("daily-2026-07-10..(末尾)");

        var (toOnly, _) = ReportKnowledgeReingestScope.Parse(false, null, "daily-2026-07-10");
        toOnly!.Includes(Report(ReportKind.Daily, new DateOnly(2020, 1, 1))).Should().BeTrue();
        toOnly.Includes(Report(ReportKind.Daily, new DateOnly(2026, 7, 11))).Should().BeFalse();

        var (all, _) = ReportKnowledgeReingestScope.Parse(true, null, "  ");
        all!.All.Should().BeTrue();
        all.Describe().Should().Be("all");
    }

    // T-10-1499: 全件は明示させる。併用・指定なし・逆順・形の違いは受けない。
    [Theory]
    [InlineData(null, null, null)]
    [InlineData(false, null, null)]
    [InlineData(true, "daily-2026-07-10", null)]
    [InlineData(true, null, "daily-2026-07-10")]
    [InlineData(null, "daily-2026-07-11", "daily-2026-07-10")]
    [InlineData(null, "monthly-2026-08", "weekly-2026-W28")]
    [InlineData(null, "daily-2026-07-XX", null)]
    [InlineData(null, null, "weekly-2026-W99")]
    public void 不正な指定は受けない(bool? all, string? from, string? to)
    {
        var (scope, error) = ReportKnowledgeReingestScope.Parse(all, from, to);
        scope.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    // T-10-1499: 本番の組み立てで、不正な指定は 400 で何もしない（監査も残さない）。範囲の指定は対象を絞る。
    [Fact]
    public async Task 本番の組み立てで不正な指定は400_範囲は対象を絞る()
    {
        var kb = new FakeKnowledgeCatalog();
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithCatalog(baseFactory, kb);
        Seed(factory.Services, "daily-2026-07-10", ReportKind.Daily, new DateOnly(2026, 7, 10), "# A");
        Seed(factory.Services, "daily-2026-08-03", ReportKind.Daily, new DateOnly(2026, 8, 3), "# B");

        var (bad, _, badAudits) = await RunAsync(factory, new { });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        badAudits.Should().BeEmpty();
        kb.CreateCalls.Should().Be(0);

        // PR #1038 の監査 3: 末日の計算が溢れる期間キーは 500 ではなく 400。
        var (overflow, _, overflowAudits) = await RunAsync(factory, new { toPeriodKey = "monthly-9999-12" });
        overflow.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        overflowAudits.Should().BeEmpty();

        var (ok, result, audits) = await RunAsync(factory, new { fromPeriodKey = "monthly-2026-08", toPeriodKey = "monthly-2026-08" });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        result!.Items.Should().ContainSingle().Which.PeriodKey.Should().Be("daily-2026-08-03");
        audits.Should().ContainSingle().Which.Scope.Should().Be("monthly-2026-08..monthly-2026-08");
    }
}
