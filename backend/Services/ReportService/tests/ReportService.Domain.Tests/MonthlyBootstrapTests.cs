using ReportService.Domain;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Domain.Tests;

// FR-06, UC-03, INDEX 決定事項16, IADR-0071 決定4: 初回月報ブートストラップ（初期監視銘柄の選定ドラフト）を純関数で生成する。
public class MonthlyBootstrapTests
{
    [Fact]
    public void 月初と月報キーを導出する()
    {
        var draft = MonthlyBootstrap.BuildDraft(new DateOnly(2026, 7, 18), ["AAPL", "7203"], assumptionsVersion: 1);

        draft.Kind.Should().Be(ReportKind.Monthly);
        draft.PeriodKey.Should().Be("monthly-2026-07");
        draft.PeriodStart.Should().Be(new DateOnly(2026, 7, 1));
        draft.BasedOn.Should().BeNull(); // 初回＝上位方針なし。
        draft.State.Should().Be(ReportState.Draft); // 確定は利用者の対話を経る。
    }

    [Fact]
    public void 方針要旨に初期監視銘柄を列挙する()
    {
        var draft = MonthlyBootstrap.BuildDraft(new DateOnly(2026, 7, 1), ["AAPL", "7203"], assumptionsVersion: 2);

        draft.PolicySummary.Should().Contain("AAPL");
        draft.PolicySummary.Should().Contain("7203");
        draft.AssumptionsVersion.Should().Be(2);
    }

    [Fact]
    public void 監視銘柄が空でも生成できる_未選定を明示()
    {
        var draft = MonthlyBootstrap.BuildDraft(new DateOnly(2026, 7, 1), [], assumptionsVersion: 1);

        draft.PolicySummary.Should().Contain("未選定");
    }

    [Fact]
    public void 決定的_同一入力で同一出力()
    {
        var a = MonthlyBootstrap.BuildDraft(new DateOnly(2026, 7, 5), ["AAPL"], 1);
        var b = MonthlyBootstrap.BuildDraft(new DateOnly(2026, 7, 5), ["AAPL"], 1);
        a.Should().Be(b);
    }
}
