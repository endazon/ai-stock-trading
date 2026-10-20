using AwesomeAssertions;
using ReportService.Domain;
using ReportService.Features.Reports;
using Xunit;

namespace ReportService.Tests;

// T-10-2944, FR-04, FR-07, FR-10, #1397, IADR-0562 決定 4: 方針の改訂の AI へ、テーマの同時保有の上限はコードが審査で強制すること（この版から有効）と、
// 方針の文では集まりや上限を変えられないことを注記させる。判断へ渡る材料に保有中の全銘柄があることも示す。
public class PolicyRevisionThemeConcentrationTests
{
    private static PolicyRevisionContext Context(ReportKind kind) =>
        new(kind, kind == ReportKind.Daily ? "daily-2026-10-20" : "weekly-2026-W43", "COIN・MARA・MSTR・HOOD は同時に 1 銘柄まで", null, "AI 銘柄も 1 つまでにしたい");

    [Fact]
    public void T_10_2944_日報の改訂にだけテーマの上限の注記がある()
    {
        PolicyRevisionPromptBuilder.Build(Context(ReportKind.Daily)).Should().Contain(PolicyRevisionPromptBuilder.ThemeConcentrationRule);
        PolicyRevisionPromptBuilder.Build(Context(ReportKind.Weekly)).Should().NotContain(PolicyRevisionPromptBuilder.ThemeConcentrationRule);
        PolicyRevisionPromptBuilder.ThemeConcentrationRule.Should()
            .Contain("コードが審査で強制する").And.Contain("この版から有効")
            .And.Contain("COIN・MARA・MSTR・HOOD").And.Contain("NVDA・SMCI")
            .And.Contain("方針の文で変えることはできない");
    }

    [Theory]
    [InlineData(ReportKind.Daily)]
    [InlineData(ReportKind.Weekly)]
    [InlineData(ReportKind.Monthly)]
    public void T_10_2944_判断へ渡る材料に保有中の全銘柄がある(ReportKind kind)
    {
        PolicyRevisionPromptBuilder.Build(Context(kind)).Should().Contain("保有中の全銘柄（銘柄・方向・数量）");
    }
}
