using FluentAssertions;
using Xunit;

namespace AiStockTrading.Report.Domain.Tests;

// FR-06, FR-07, ADR-0003, IADR-0115 決定4, #280: 自動生成ドラフトの方針文（純関数）を検証する。
// 自動生成では新しい方針を機械が提案せず、直近の確定済み方針の「継続案」に留める（確定は利用者の対話・ADR-0003）。
public class ReportPolicyDraftTests
{
    // 期間表記は自然キー（ReportPeriod.ExpectedKey）そのもの。定数に括り出して各テストの意図を読みやすくする。
    private const string ParentWeekly = "weekly-2026-W31";
    private const string PreviousDaily = "daily-2026-07-28";

    [Fact]
    public void 未確定である旨を必ず明記する()
    {
        var policy = ReportPolicyDraft.CarryOver(ReportKind.Daily, PreviousDaily, "押し目買い", ParentWeekly);

        policy.Should().Contain("未確定");
    }

    [Fact]
    public void 直近の確定済み方針を継続案として引き継ぐ()
    {
        var policy = ReportPolicyDraft.CarryOver(ReportKind.Daily, PreviousDaily, "押し目買い・上限 3 銘柄", ParentWeekly);

        policy.Should().Contain(PreviousDaily);
        policy.Should().Contain("押し目買い・上限 3 銘柄");
    }

    [Fact]
    public void 継続元が無ければ方針の記入を促す()
    {
        var policy = ReportPolicyDraft.CarryOver(ReportKind.Daily, previousPeriodKey: null, previousPolicy: null, parentPeriodKey: ParentWeekly);

        policy.Should().Contain("記入");
        policy.Should().NotContain("継続する案");
    }

    [Fact]
    public void 継続元の方針文が空白のみなら継続案にしない()
    {
        var policy = ReportPolicyDraft.CarryOver(ReportKind.Daily, PreviousDaily, "   ", ParentWeekly);

        policy.Should().Contain("記入");
        policy.Should().NotContain(PreviousDaily);
    }

    [Fact]
    public void 上位方針が未確定ならその旨を明記する()
    {
        // 03_reporting-cycle「上位方針の欠落」: 参照できない場合はドラフトに明記する。
        var policy = ReportPolicyDraft.CarryOver(ReportKind.Daily, PreviousDaily, "押し目買い", parentPeriodKey: null);

        policy.Should().Contain("週報");
        policy.Should().Contain("未確定");
    }

    [Fact]
    public void 上位方針を参照できていれば欠落の注記は入らない()
    {
        var policy = ReportPolicyDraft.CarryOver(ReportKind.Daily, PreviousDaily, "押し目買い", ParentWeekly);

        policy.Should().NotContain("上位方針（週報）は未確定");
    }

    [Theory]
    [InlineData(ReportKind.Daily, "日報", "週報")]
    [InlineData(ReportKind.Weekly, "週報", "月報")]
    [InlineData(ReportKind.Monthly, "月報", "前月の月報")]
    public void 種別ごとに自種別と上位種別の呼称を使い分ける(ReportKind kind, string self, string parent)
    {
        var withParent = ReportPolicyDraft.CarryOver(kind, "prev-key", "前期の方針", "parent-key");
        var withoutParent = ReportPolicyDraft.CarryOver(kind, "prev-key", "前期の方針", null);

        withParent.Should().Contain(self);
        withoutParent.Should().Contain(parent);
    }

    [Fact]
    public void 月報の上位は前月の月報であり自種別と同じ呼称にならない()
    {
        ReportPolicyDraft.ParentKind(ReportKind.Daily).Should().Be(ReportKind.Weekly);
        ReportPolicyDraft.ParentKind(ReportKind.Weekly).Should().Be(ReportKind.Monthly);
        ReportPolicyDraft.ParentKind(ReportKind.Monthly).Should().Be(ReportKind.Monthly);
    }
}
