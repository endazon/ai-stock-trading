using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using ReportService.Domain;
using ReportService.Features.Reports;
using Xunit;

namespace ReportService.Tests;

// T-10-2935, FR-04, FR-07, FR-10, #1396, IADR-0561 決定 4: 方針の改訂の AI へ、閉場前の締め切りはコードが強制することと、閉場前の手仕舞いの有効化・
// 持ち越しの指定の書式の行を示す（日報の改訂だけ）。案内の例の行は判断側が読む共有カーネル（PolicyPreCloseLines）で読める。
public class PolicyRevisionPreCloseRuleTests
{
    private static PolicyRevisionContext Context(ReportKind kind) =>
        new(kind, kind == ReportKind.Daily ? "daily-2026-10-20" : "weekly-2026-W43", "現状維持", null, "持ち越しは NVDA だけにしたい");

    [Fact]
    public void T_10_2935_日報の改訂にだけ閉場前の書式の案内がある()
    {
        PolicyRevisionPromptBuilder.Build(Context(ReportKind.Daily))
            .Should().Contain(PolicyRevisionPromptBuilder.PreCloseHeading).And.Contain(PolicyRevisionPromptBuilder.PreCloseRule);
        PolicyRevisionPromptBuilder.Build(Context(ReportKind.Weekly)).Should().NotContain(PolicyRevisionPromptBuilder.PreCloseHeading);
        PolicyRevisionPromptBuilder.PreCloseRule.Should().Contain("コードが審査で拒否する").And.Contain("既定で無効");
    }

    [Fact]
    public void T_10_2935_案内の例の行は共有カーネルで読める()
    {
        PolicyRevisionPromptBuilder.PreCloseRule.Should().Contain("「閉場前の手仕舞い: 有効」").And.Contain("「持ち越し可: NVDA, AAPL」");

        PolicyPreCloseLines.FlattenEnabled("閉場前の手仕舞い: 有効").Should().BeTrue();
        PolicyPreCloseLines.OvernightAllowed("持ち越し可: NVDA, AAPL").Should().BeEquivalentTo(["NVDA", "AAPL"]);
        // 案内が「読まれない」と示す自由文は、実際に読まれない。
        PolicyPreCloseLines.FlattenEnabled("終盤は手仕舞いを優先する").Should().BeFalse();
    }
}
