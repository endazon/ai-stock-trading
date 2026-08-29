using ReportService.Domain;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-07, FR-16, FR-17, #338, INDEX 決定29 / 決定43, IADR-0252:
// 目標値の **YAML ブロック併記**を固定する。
//
// 🔴 計画の明文（決定29）: 「翌営業日の売買条件（対象・条件・上限）は YAML を正とし、表は表示用とする。
// **取引判断サービスは YAML ブロックのみを読む**（Markdown 表のパースはしない）。」
public class ReportPolicyYamlTests
{
    private static ReportView View(ReportKind kind = ReportKind.Daily, DateTimeOffset? confirmedAt = null) => new()
    {
        Kind = kind,
        PeriodKey = "daily-2026-08-28",
        PeriodLabel = "2026-08-28",
        AssumptionsVersion = 3,
        BasedOn = "weekly-2026-W35",
        ConfirmedAt = confirmedAt,
        Pnl = new PnlSummary(0, 0, 0, 0, 0, 0, 0, 0),
        PolicySummary = "監視銘柄は AAPL・TSLA。",
    };

    [Fact]
    public void 機械可読なYAMLフェンスとして出力する()
    {
        var yaml = ReportPolicyYaml.Render(View());

        yaml.Should().StartWith("```yaml\n");
        yaml.Should().EndWith("```\n");
    }

    [Fact]
    public void 報告書の識別子と前提条件バージョンと機密区分を持つ()
    {
        var yaml = ReportPolicyYaml.Render(View(ReportKind.Monthly));

        yaml.Should().Contain("report_type: monthly");
        yaml.Should().Contain("period: 2026-08-28");
        yaml.Should().Contain("based_on: weekly-2026-W35");
        // FR-17: 適用した全体前提条件のバージョン。
        yaml.Should().Contain("assumptions_version: v3");
        // INDEX 決定43: 機密区分は internal で確定。
        yaml.Should().Contain("confidentiality: internal");
    }

    // 🔴 #310 の再発防止。確定済みの報告書が YAML で「未確定」を名乗ると、
    // 取引判断サービスが確定済み方針を読み飛ばす（またはドラフトを方針として採る）経路ができる。
    [Theory]
    [InlineData(false, "status: draft")]
    [InlineData(true, "status: fixed")]
    public void 状態は確定状態に従う(bool confirmed, string expected)
    {
        var yaml = ReportPolicyYaml.Render(
            View(confirmedAt: confirmed ? new DateTimeOffset(2026, 8, 28, 9, 0, 0, TimeSpan.Zero) : null));

        yaml.Should().Contain(expected);
    }

    // 🔴 **否定形**: 散文の方針本文が YAML の売買条件へ書き起こされない。
    // 書き起こせば、それは**機械が発明した取引条件**であり FR-16 に反する。
    // **対の肯定形**: 未供給であることが明示され、「条件なし」と読ませない。
    [Fact]
    public void 散文の方針を売買条件へ書き起こさず未供給と明示する()
    {
        var yaml = ReportPolicyYaml.Render(View());

        yaml.Should().NotContain("AAPL");      // 否定形: 方針本文が YAML へ漏れていない
        yaml.Should().Contain("trading_conditions: null"); // 肯定形: 未供給と明示している
        yaml.Should().Contain("未供給であり「条件なし」ではない");
        // 空配列にすると「条件が無いことが確定した」と読める。
        yaml.Should().NotContain("trading_conditions: []");
    }

    [Fact]
    public void 上位方針が無ければnullと書く()
    {
        var view = View() with { BasedOn = null };

        ReportPolicyYaml.Render(view).Should().Contain("based_on: null");
    }
}
