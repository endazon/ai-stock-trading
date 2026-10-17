using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using ReportService.Domain;
using ReportService.Features.Reports;
using Xunit;

namespace ReportService.Tests;

// FR-04, FR-07, ADR-0003, #1384, IADR-0557 決定 8: 方針の改訂の AI へ、保有中の建玉には建てた判断の記録（条件の記号と基準価格）が
// ロットごとに渡ることと、「条件 X:」書式の行の書き方を示す。PoC（2026-10-17）では改訂の AI が「記録に依存する」と注記するしかなかった。
public class PolicyRevisionEntryBasisMaterialTests
{
    private static PolicyRevisionContext Context(ReportKind kind) =>
        new(kind, kind == ReportKind.Daily ? "daily-2026-10-17" : "weekly-2026-W42", "現状維持", null, "B で建てた建玉は安値割れで撤退したい");

    // #1384 AC-07-01: どの報告書の改訂でも、判断へ渡る材料に建玉ごとの建てた条件と基準価格（記録が無ければ不明）がある。
    [Theory]
    [InlineData(ReportKind.Daily)]
    [InlineData(ReportKind.Weekly)]
    [InlineData(ReportKind.Monthly)]
    public void 判断へ渡る材料に建玉ごとの建てた条件と基準価格がある(ReportKind kind)
    {
        var prompt = PolicyRevisionPromptBuilder.Build(Context(kind));

        prompt.Should().Contain(PolicyRevisionPromptBuilder.HeldPositionMaterial);
        PolicyRevisionPromptBuilder.HeldPositionMaterial.Should().Contain("条件 X:").And.Contain("基準価格").And.Contain("不明").And.Contain("既定の出口");
    }

    // #1384 AC-07-02: 日報の改訂にだけ「条件 X:」書式の案内と、建てた条件で出口を分けるときの案内（既定の出口も書く）がある。
    [Fact]
    public void 日報の改訂にだけ条件の記号の書き方の案内がある()
    {
        var daily = PolicyRevisionPromptBuilder.Build(Context(ReportKind.Daily));
        var weekly = PolicyRevisionPromptBuilder.Build(Context(ReportKind.Weekly));

        daily.Should().Contain(PolicyRevisionPromptBuilder.EntryConditionHeading)
            .And.Contain(PolicyRevisionPromptBuilder.EntryConditionLineRule)
            .And.Contain(PolicyRevisionPromptBuilder.EntryConditionExitRule);
        weekly.Should().NotContain(PolicyRevisionPromptBuilder.EntryConditionHeading);
        PolicyRevisionPromptBuilder.EntryConditionExitRule.Should().Contain("既定の出口");
    }

    // #1384 AC-07-03: 案内の例の行は、判断側が読む共有カーネル（PolicyEntryConditions）で記号として読める（書式の食い違いを作らない）。
    [Fact]
    public void 案内の例の行は共有カーネルで記号として読める()
    {
        PolicyEntryConditions.Symbols("条件 A: AAPL 前日比 -3% 以下で買い\n買い条件 B（急落反発）: 日中安値から +1% 戻したら買い")
            .Should().BeEquivalentTo(["A", "B"]);
        PolicyRevisionPromptBuilder.EntryConditionLineRule.Should().Contain("「条件 A: AAPL 前日比 -3% 以下で買い」")
            .And.Contain("「買い条件 B（急落反発）: 日中安値から +1% 戻したら買い」");
    }
}
