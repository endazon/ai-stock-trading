using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using ReportService.Domain;
using ReportService.Features.Reports;
using Xunit;

namespace ReportService.Tests;

// T-06-077〜T-06-080, FR-06, FR-16, 計画 ADR-0059 決定 1・2, #1218, IADR-0519 決定 1・2: 週報 §6 の週次目標の書式行（「数値目標:」行）の文法と、
// 書式どおりの行が無い週報の方針への確定前の警告。🔴 自由文からは読まない。
public class WeeklyGoalLineTests
{
    // ---- T-06-077: 書式どおりの行を読む ----

    // Given 週報の方針に書式どおりの「数値目標:」行 When 読む Then 下限・上限・単位を返す（全角・箇条書き・カンマ・小数・区切りの揺れを含む）。
    [Theory]
    [InlineData("数値目標: -200 〜 +500 USD", -200, 500)]
    [InlineData("- 数値目標: -200〜+500 USD", -200, 500)]
    [InlineData("・数値目標：－２００～＋５００ ＵＳＤ", -200, 500)]
    [InlineData("* 数値目標:0 ~ 1,000 USD", 0, 1000)]
    [InlineData("  数値目標: -1,250.5 〜 2,000.25 USD  ", -1250.5, 2000.25)]
    [InlineData("数値目標: 100 〜 100 USD", 100, 100)]
    [InlineData("翌週は押し目買いを優先する。\n数値目標: -300 〜 +800 USD\n重点: 半導体", -300, 800)]
    public void 書式どおりの行を読む(string policy, double lower, double upper)
    {
        var reading = WeeklyGoalLine.Parse(policy);

        reading.Status.Should().Be(WeeklyGoalLineStatus.Conforming);
        reading.IsConforming.Should().BeTrue();
        reading.Lower.Should().Be((decimal)lower);
        reading.Upper.Should().Be((decimal)upper);
        reading.Unit.Should().Be("USD");
    }

    // 案内（改訂のプロンプト・警告）に載せる例は、この文法でそのまま読める（例を写した方針が警告されない）。
    [Fact]
    public void 案内の例は文法で読める()
    {
        foreach (var example in WeeklyGoalLine.Examples)
            WeeklyGoalLine.Parse(example).IsConforming.Should().BeTrue(example);
    }

    // ---- T-06-078（否定形）: 書式外は値を返さない ----

    // Given 単位なし・範囲なし・下限 > 上限・後ろに文字・小数 3 桁・$ 記号・行が 2 つ・見出しや説明の文 When 読む Then 書式外。
    [Theory]
    [InlineData("数値目標: -200 〜 +500")]
    [InlineData("数値目標: +500 USD")]
    [InlineData("数値目標: 500 〜 -200 USD")]
    [InlineData("数値目標: -200 〜 +500 USD を目指す")]
    [InlineData("数値目標: -200 〜 +500.125 USD")]
    [InlineData("数値目標: $-200 〜 $500")]
    [InlineData("数値目標: -200 〜 +500 ドル")]
    [InlineData("数値目標: 1,00 〜 500 USD")]
    [InlineData("数値目標: -200 〜 +500 usd")]
    [InlineData("**数値目標:** -200 〜 +500 USD")]
    [InlineData("数値目標 -200 〜 +500 USD")]
    [InlineData("数値目標: -200 〜 +500 USD\n数値目標: 0 〜 +300 USD")]
    [InlineData("## 数値目標\n数値目標: -200 〜 +500 USD")]
    [InlineData("数値目標は控えめに置く。\n数値目標: -200 〜 +500 USD")]
    [InlineData("今週は利益を +500 USD 程度の数値目標とする。")]
    public void 書式外の行は読まない(string policy)
    {
        var reading = WeeklyGoalLine.Parse(policy);

        reading.Status.Should().Be(WeeklyGoalLineStatus.Malformed);
        reading.Lower.Should().BeNull();
        reading.Upper.Should().BeNull();
        reading.IsConforming.Should().BeFalse();
    }

    // T-06-078（否定形）, FR-06, FR-16, #1218（監査 Y1）, IADR-0519 決定 1: ASCII 以外の数字（アラビア・インド数字。NFKC でも ASCII へ寄らない）は
    // 数字として読まない。例外で生成を落とさず書式外にする。
    [Theory]
    [InlineData("数値目標: ٣ 〜 5 USD")]
    [InlineData("数値目標: १२ 〜 50 USD")]
    public void ASCII以外の数字は書式外として読まない(string policy)
    {
        var reading = WeeklyGoalLine.Parse(policy);

        reading.Status.Should().Be(WeeklyGoalLineStatus.Malformed);
        reading.Lower.Should().BeNull();
        reading.Upper.Should().BeNull();
    }

    [Fact]
    public void 候補が2行以上なら行数を返す()
    {
        WeeklyGoalLine.Parse("数値目標: -200 〜 +500 USD\n数値目標: 0 〜 +300 USD").CandidateCount.Should().Be(2);
    }

    // ---- T-06-079: 行なし・単位違い ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("翌週は押し目買いを優先する。目標は +500 USD。")]
    public void 数値目標の語が無ければ行なし(string? policy)
    {
        var reading = WeeklyGoalLine.Parse(policy);

        reading.Status.Should().Be(WeeklyGoalLineStatus.Missing);
        reading.CandidateCount.Should().Be(0);
    }

    // Given 単位が円・JPY（書式どおり）When 読む Then 単位違い（換算しない。計画 ADR-0059 決定 3）。範囲は読めるが照合には使わない。
    [Theory]
    [InlineData("数値目標: -30,000 〜 +75,000 円", "円")]
    [InlineData("数値目標: -30000 〜 75000 JPY", "JPY")]
    public void 基準通貨でない単位は単位違い(string policy, string unit)
    {
        var reading = WeeklyGoalLine.Parse(policy);

        reading.Status.Should().Be(WeeklyGoalLineStatus.UnitMismatch);
        reading.Unit.Should().Be(unit);
        reading.IsConforming.Should().BeFalse();
    }
}

// T-06-080, FR-06, FR-07, 計画 ADR-0059 決定 2, #1218, IADR-0519 決定 2: 書式どおりの行が無い週報の方針は確定の前に警告する（確定は止めない）。
public class WeeklyGoalLineCheckTests
{
    // Given 書式どおりの行が無い（行なし・書式外・単位違い）週報の方針 When 判定 Then 印つきの警告（理由と直し方を含む）。
    [Theory]
    [InlineData("押し目買いを優先する。", "行がありません")]
    [InlineData("数値目標: +500 USD", "行が書式に合いません")]
    [InlineData("数値目標: -200 〜 +500 USD\n数値目標の補足: 控えめに", "2 行あります")]
    [InlineData("数値目標: -30,000 〜 +75,000 円", "単位が基準通貨 USD ではありません（円）")]
    public void 書式どおりの行が無い週報の方針は警告する(string policy, string reason)
    {
        var warning = WeeklyGoalLineCheck.WarningFor(ReportKind.Weekly, policy);

        warning.Should().StartWith(ReportSummaryMarkers.WeeklyGoalLineMissingPrefix)
            .And.Contain(reason)
            .And.Contain("確定はできます")
            .And.Contain("数値目標: -200 〜 +500 USD", "直し方（行の書式の例）まで書く");
    }

    [Fact]
    public void 書式どおりの行がある週報の方針は警告しない()
    {
        WeeklyGoalLineCheck.WarningFor(ReportKind.Weekly, "押し目買いを優先する。\n数値目標: -200 〜 +500 USD").Should().BeNull();
    }

    // （否定形）日報・月報の方針には出さない（週次目標の書式行は週報だけの要件）。
    [Theory]
    [InlineData(ReportKind.Daily)]
    [InlineData(ReportKind.Monthly)]
    public void 日報と月報には出さない(ReportKind kind)
    {
        WeeklyGoalLineCheck.WarningFor(kind, "押し目買いを優先する。").Should().BeNull();
    }

    // 改訂のプロンプトは週報にだけ書式を案内し、データ（利用者の指示）より前に置く。案内の例は文法で読める（T-06-088）。
    [Fact]
    public void 週報の改訂のプロンプトに書式を案内する()
    {
        var weekly = PolicyRevisionPromptBuilder.Build(
            new PolicyRevisionContext(ReportKind.Weekly, "weekly-2026-W41", "押し目買い", null, "目標を決めたい"));
        var daily = PolicyRevisionPromptBuilder.Build(
            new PolicyRevisionContext(ReportKind.Daily, "daily-2026-10-06", "押し目買い", null, "指示"));

        weekly.Should().Contain(PolicyRevisionPromptBuilder.WeeklyGoalLineHeading).And.Contain(PolicyRevisionPromptBuilder.WeeklyGoalLineRule);
        weekly.IndexOf(PolicyRevisionPromptBuilder.WeeklyGoalLineHeading, StringComparison.Ordinal)
            .Should().BeLessThan(weekly.IndexOf("ownerInstruction:", StringComparison.Ordinal));
        PolicyRevisionPromptBuilder.WeeklyGoalLineRule.Should().Contain("数値目標: <下限> 〜 <上限> USD").And.Contain("円で書くと照合できない");
        daily.Should().NotContain(PolicyRevisionPromptBuilder.WeeklyGoalLineHeading);
    }
}
