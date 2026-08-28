using AwesomeAssertions;
using Xunit;

namespace ReportService.Domain.Tests;

// FR-06, FR-07, #338, #310, INDEX 決定29, IADR-0252:
// **確定した報告書の本文が「未確定」を名乗り続けない**ことを固定する。
//
// 🔴 決定29 は「取引判断サービスは YAML ブロックのみを読む」と定めた。読む側が状態を YAML から
// 判断する以上、確定した事実が YAML に反映されないことは**方針が採用されない／ドラフトが採用される**
// のいずれかの誤りへ直結する。#310 と同じ形の事故である。
public class ReportBodyStatusTests
{
    private static readonly DateTimeOffset ConfirmedAt = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private const string Draft = """
        ---
        report_type: daily
        status: draft
        confirmed_at: null
        ---

        # 日報 2026-08-28

        ## 3. 翌営業日の方針

        監視銘柄は AAPL。status: draft という語を含む散文もあり得る。

        ```yaml
        report_type: daily
        status: draft
        ```
        """;

    // 🔴 frontmatter と YAML ブロックの**両方**を揃える。片方だけだと読む側で食い違う。
    [Fact]
    public void フロントマターとYAMLブロックの状態を確定へ揃える()
    {
        var confirmed = ReportBodyStatus.MarkConfirmed(Draft, ConfirmedAt)!;
        var lines = confirmed.Split('\n');

        // 2 箇所（frontmatter と YAML ブロック）が両方とも fixed になる。
        lines.Count(l => l == "status: fixed").Should().Be(2);
        lines.Should().NotContain("status: draft");

        // 🔴 **散文に現れる同じ語は書き換えない**（状態の行だけが対象である）。
        // 行そのものが状態表記であるときだけ倒すことで、利用者が書いた本文を変えない。
        confirmed.Should().Contain("status: draft という語を含む散文もあり得る。");
    }

    [Fact]
    public void 確定日時を記録する()
    {
        var confirmed = ReportBodyStatus.MarkConfirmed(Draft, ConfirmedAt)!;

        confirmed.Should().Contain("confirmed_at: 2026-08-28T12:00:00.0000000+00:00");
        confirmed.Should().NotContain("confirmed_at: null");
    }

    // 🔴 **状態の行以外は 1 バイトも変えない。** 確定とは「その本文でよい」と承認した行為であり、
    // 散文を作り直してはならない（作り直すと承認した内容と違うものが残る）。
    [Fact]
    public void 状態の行以外は変更しない()
    {
        var confirmed = ReportBodyStatus.MarkConfirmed(Draft, ConfirmedAt)!;

        confirmed.Should().Contain("# 日報 2026-08-28");
        confirmed.Should().Contain("監視銘柄は AAPL。");
        confirmed.Should().Contain("## 3. 翌営業日の方針");
        // 行数は変わらない（行の追加・削除をしない）。
        confirmed.Split('\n').Should().HaveCount(Draft.ReplaceLineEndings("\n").Split('\n').Length);
    }

    // 冪等（二重確定・再送で結果が変わらない）。
    [Fact]
    public void 冪等である()
    {
        var once = ReportBodyStatus.MarkConfirmed(Draft, ConfirmedAt);
        var twice = ReportBodyStatus.MarkConfirmed(once, ConfirmedAt);

        twice.Should().Be(once);
    }

    // 🔴 null は null のまま（「本文が無い」と「本文が空」を潰さない）。
    [Fact]
    public void 本文がnullなら_nullのまま返す()
    {
        ReportBodyStatus.MarkConfirmed(null, ConfirmedAt).Should().BeNull();
        ReportBodyStatus.MarkConfirmed(string.Empty, ConfirmedAt).Should().BeEmpty();
    }

    // 🔴 **対の肯定形**: 描画した本文をそのまま通すと、確定後の YAML が `fixed` になる。
    [Fact]
    public void 描画した本文を確定へ揃えるとYAMLがfixedになる()
    {
        var view = new ReportView
        {
            Kind = ReportKind.Daily,
            PeriodKey = "daily-2026-08-28",
            PeriodLabel = "2026-08-28",
            AssumptionsVersion = 1,
            Pnl = new PnlSummary(0, 0, 0, 0, 0, 0, 0, 0),
            PolicySummary = "方針",
        };

        var draft = ReportRenderer.RenderMarkdown(view);
        draft.Should().Contain("status: draft"); // 前提: ドラフトは draft を名乗る

        var confirmed = ReportBodyStatus.MarkConfirmed(draft, ConfirmedAt)!;

        confirmed.Should().NotContain("status: draft");
        confirmed.Should().Contain("status: fixed");
    }
}
