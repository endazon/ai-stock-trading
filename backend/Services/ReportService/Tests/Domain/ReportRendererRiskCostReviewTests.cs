using ReportService.Domain;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-07, FR-16, FR-17, #615, IADR-0305, 04_report-templates 週報 §5「リスク・費用レビュー」:
// **出口（`ReportRenderer` の本文）で** 費用の内訳・費用率が出ることを固定する。
//
// 🔴 **純関数のテスト（PeriodCostReviewTests）だけでは、結線が無くても緑になる**（IADR-0269 決定1）。
// 全文の固定は `ReportTemplateGoldenTests` が担う。
public class ReportRendererRiskCostReviewTests
{
    private static ReportView Weekly(PeriodCostReview? review) => new()
    {
        Kind = ReportKind.Weekly,
        PeriodKey = "weekly-2026-W35",
        PeriodLabel = "2026-W35",
        Markets = ["JP", "US"],
        AssumptionsVersion = 3,
        Pnl = new PnlSummary(2_000m, 100m, 380m, 1_520m, 0m, 4, 2, 1),
        Narrative = "散文。",
        PolicySummary = "方針。",
        CostReview = review,
    };

    private static PeriodCostReview Review(decimal realizedGross, decimal? ratio) =>
        new(Commission: 80m, FxSpread: 20m, TotalCost: 100m,
            TaxWithheld: 380m, RealizedPnlGross: realizedGross, CostRatio: ratio);

    // --- 結線（出口に出ること） ---

    [Fact]
    public void 週報の本文に費用の内訳と費用率が出る()
    {
        var md = ReportRenderer.RenderMarkdown(Weekly(Review(2_000m, 0.05m)));

        var section = Section(md, "## 5. リスク・費用レビュー", "## 6. 翌週の方針");
        section.Should().Contain("| 費用の区分 | 金額 |");
        section.Should().Contain("| 売買手数料 | +80.00 USD |");
        section.Should().Contain("| 為替スプレッド相当額 | +20.00 USD |");
        section.Should().Contain("| 費用合計（§1 と同じ値） | +100.00 USD |");
        section.Should().Contain("| 源泉徴収税額 | +380.00 USD |");
        section.Should().Contain("損益に対する費用率: 5.0%（費用合計 +100.00 USD ÷ 実現損益〔税引前・費用前〕 +2,000.00 USD）");
        section.Should().NotContain("本節は未実装です");
    }

    // 🔴 **「諸費用」は記録源が無い。0 と書くと「費用が発生しなかった」と読める。**
    [Fact]
    public void 取引諸費用は0ではなく未供給と描く()
    {
        var section = Section(
            ReportRenderer.RenderMarkdown(Weekly(Review(2_000m, 0.05m))),
            "## 5. リスク・費用レビュー", "## 6. 翌週の方針");

        section.Should().Contain("| 取引諸費用 | **未供給** |");
        section.Should().NotContain("| 取引諸費用 | 0");
        section.Should().Contain("**「取引諸費用」（米国株の SEC Fee・TAF 等）は記録源がありません。**");
        section.Should().Contain("**費用合計は諸費用のぶんだけ過小です。**");
    }

    // 🔴 **記録源が無い 3 項目を行ごと落とさない**（ADR-0030 決定3 と同じ理由）。
    [Fact]
    public void 損切り執行と発注拒否と上限使用率は理由つきで未供給と描く()
    {
        var section = Section(
            ReportRenderer.RenderMarkdown(Weekly(Review(2_000m, 0.05m))),
            "## 5. リスク・費用レビュー", "## 6. 翌週の方針");

        section.Should().Contain("- 損切り執行: **未供給** / 発注拒否: **未供給** / 上限使用率の週間最大: **未供給**");
        section.Should().Contain("**「損切りが 0 件だった」ではありません。**");
        section.Should().Contain("**約定の記録には拒否が現れません**");
        section.Should().Contain("**上限使用率の週間最大**: 算出元が本サービスにも取引管理サービスにもありません");
    }

    // --- 費用率の分母（0 以下は算出不能であり 0% ではない） ---

    [Fact]
    public void 分母が0以下なら費用率を算出不能と描き0パーセントとは書かない()
    {
        var section = Section(
            ReportRenderer.RenderMarkdown(Weekly(Review(-2_000m, null))),
            "## 5. リスク・費用レビュー", "## 6. 翌週の方針");

        section.Should().Contain("損益に対する費用率: **算出不能**（分母となる実現損益〔税引前・費用前〕が -2,000.00 USD で、0 以下です）");
        section.Should().Contain("**0% ではありません。**");
    }

    [Fact]
    public void 費用率の分母が税引前かつ費用前であることを凡例で明示する()
    {
        var md = ReportRenderer.RenderMarkdown(Weekly(Review(2_000m, 0.05m)));

        md.Should().Contain("費用率の**分母は実現損益（税引前・費用前）**です。");
        md.Should().Contain("**§1 の「週間実現損益（税引後・費用込み）」は分母に採れません**");
    }

    // --- 未供給と 0 の区別（潰さない） ---

    [Fact]
    public void 内訳が未供給なら費用0と区別して節ごと出す()
    {
        var md = ReportRenderer.RenderMarkdown(Weekly(null));

        md.Should().Contain("## 5. リスク・費用レビュー");
        md.Should().Contain("**費用の内訳を組み立てられませんでした（供給元がありません）**");
        md.Should().NotContain("| 費用の区分 | 金額 |");
        // 未供給でも、記録源が無い 3 項目は理由つきで出る（節の意味が空にならない）。
        md.Should().Contain("- 損切り執行: **未供給** / 発注拒否: **未供給** / 上限使用率の週間最大: **未供給**");
    }

    [Fact]
    public void 週報以外ではリスク費用レビューを出さない()
    {
        foreach (var kind in new[] { ReportKind.Daily, ReportKind.Monthly })
        {
            var md = ReportRenderer.RenderMarkdown(Weekly(Review(2_000m, 0.05m)) with
            {
                Kind = kind,
                PeriodKey = kind == ReportKind.Daily ? "daily-2026-08-28" : "monthly-2026-08",
                PeriodLabel = kind == ReportKind.Daily ? "2026-08-28" : "2026-08",
            });

            md.Should().NotContain("## 5. リスク・費用レビュー");
            md.Should().NotContain("| 費用の区分 | 金額 |");
        }
    }

    private static string Section(string markdown, string from, string to)
    {
        var start = markdown.IndexOf(from, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = markdown.IndexOf(to, start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        return markdown[start..end];
    }
}
