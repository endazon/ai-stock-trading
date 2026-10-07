using ReportService.Domain;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-07, FR-16, FR-17, #615, IADR-0305, 計画 ADR-0035, #1201, IADR-0501, 04_report-templates 週報 §5「リスク・費用レビュー」:
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

    // 計画 ADR-0035 決定 3・4, #1201: 為替スプレッド（入出金時の実績）は未供給、借株料は引数で与える。
    private static PeriodCostReview Review(decimal tradeValueDifference, decimal? ratio, decimal? borrowFee = null, int unrecorded = 0) =>
        new(Commission: 80m, RegulatoryFees: 20m,
            Total: new PeriodCostTotal(100m, FxSpread: null, BorrowFee: borrowFee, BorrowFeeUnrecordedCount: unrecorded),
            TaxWithheld: 380m, TradeValueDifference: tradeValueDifference, CostRatio: ratio);

    // --- 結線（出口に出ること） ---

    [Fact]
    public void 週報の本文に費用の内訳と費用率が出る()
    {
        var md = ReportRenderer.RenderMarkdown(Weekly(Review(2_000m, 0.05m)));

        var section = Section(md, "## 5. リスク・費用レビュー", "## 6. 翌週の方針");
        section.Should().Contain("| 費用の区分 | 金額 |");
        section.Should().Contain("| 売買手数料 | +80.00 USD |");
        section.Should().Contain("| 取引諸費用 | +20.00 USD |");
        section.Should().Contain("| 費用合計（§1 と同じ値） | +100.00 USD |");
        section.Should().Contain("| 源泉徴収税額 | +380.00 USD |");
        section.Should().Contain("損益に対する費用率: 5.0%（費用合計 +100.00 USD ÷ 約定代金差額 +2,000.00 USD）");
        section.Should().NotContain("本節は未実装です");
    }

    // FR-06, 計画 ADR-0035 決定 5, #1201: 諸費用は設定点（計画 §2 の暫定値）から算出した値を描く。「記録源が無い」とは書かない。
    [Fact]
    public void 取引諸費用は算出した値を描き記録源が無いとは書かない()
    {
        var section = Section(
            ReportRenderer.RenderMarkdown(Weekly(Review(2_000m, 0.05m))),
            "## 5. リスク・費用レビュー", "## 6. 翌週の方針");

        section.Should().Contain("| 取引諸費用 | +20.00 USD |");
        section.Should().NotContain("| 取引諸費用 | **未供給** |");
        section.Should().Contain("**SEC 手数料（Section 31）と FINRA 取引活動料（TAF）**");
        section.Should().NotContain("記録源がありません。**全体前提条件に設定点が無く");
    }

    // FR-06, 計画 ADR-0035 決定 4, #1201: 事後集計の為替スプレッドは入出金時の両替の実績。供給元が無いので未供給（0 と書かない）。
    [Fact]
    public void 為替スプレッドは入出金時の実績であり無ければ未供給と描く()
    {
        var section = Section(
            ReportRenderer.RenderMarkdown(Weekly(Review(2_000m, 0.05m))),
            "## 5. リスク・費用レビュー", "## 6. 翌週の方針");

        section.Should().Contain("| 為替スプレッド相当額 | **未供給** |");
        section.Should().Contain("**入出金時の両替**にだけ掛かる費用です");
        section.Should().Contain("**約定ごとの見積りは費用合計に含めていません**");
    }

    // FR-06, 計画 ADR-0035 決定 3, #1201: 借株料は費用合計の区分。未供給は 0 を積まず、過小である旨を凡例に書く。
    [Fact]
    public void 借株料は費用の区分に出し未供給なら過小である旨を書く()
    {
        var unsupplied = Section(
            ReportRenderer.RenderMarkdown(Weekly(Review(2_000m, 0.05m))),
            "## 5. リスク・費用レビュー", "## 6. 翌週の方針");
        unsupplied.Should().Contain("| 借株料 | **未供給** |");
        unsupplied.Should().Contain("**未供給・未計上の区分は 0 円として費用合計へ足していません——費用合計はそのぶんだけ過小です。**");

        var supplied = Section(
            ReportRenderer.RenderMarkdown(Weekly(Review(2_000m, 0.05m, borrowFee: 1.64m, unrecorded: 1))),
            "## 5. リスク・費用レビュー", "## 6. 翌週の方針");
        supplied.Should().Contain("| 借株料 | +1.64 USD〔未計上 1 件〕 |");
        supplied.Should().Contain("| 費用合計（§1 と同じ値） | +101.64 USD |");
    }

    // FR-06, 計画 ADR-0035 決定 3, #1201: §1 のラベルは計画の 4 区分。値は費用レビューと同じで、未供給の区分を名指しする。
    [Fact]
    public void 週報の第1節の費用合計は計画の4区分のラベルで借株料を含み過小である旨を書く()
    {
        var md = ReportRenderer.RenderMarkdown(Weekly(Review(2_000m, 0.05m, borrowFee: 1.64m)));

        md.Should().Contain("| 費用合計（手数料・諸費用・為替スプレッド・借株料） | +101.64 USD（うち 為替スプレッド **未供給**・借株料 +1.64 USD。"
            + "**未供給・未計上の区分を含まないため、費用合計は過小です**） |");
        md.Should().NotContain("費用合計（手数料・諸費用・為替）");
    }

    // FR-06, 計画 ADR-0035 フォローアップ 2, #1201: 月報 §1 の費用率は週報 §5 と同じ規則で埋まる（「データ連携後」ではない）。
    [Fact]
    public void 月報の第1節の費用率は約定代金差額を分母に描く()
    {
        var md = ReportRenderer.RenderMarkdown(Weekly(Review(2_000m, 0.05m)) with
        {
            Kind = ReportKind.Monthly,
            PeriodKey = "monthly-2026-08",
            PeriodLabel = "2026-08",
        });

        md.Should().Contain("| 費用合計 / 費用率 | +100.00 USD（うち 為替スプレッド **未供給**・借株料 **未供給**。"
            + "**未供給・未計上の区分を含まないため、費用合計は過小です**） / 5.0%（費用合計 +100.00 USD ÷ 約定代金差額 +2,000.00 USD） |");
        md.Should().NotContain("| 費用合計 / 費用率 | +100.00 USD / （データ連携後） |");
    }

    // FR-06, 計画 ADR-0035 決定 1, #1201: 🔴 禁じられた呼称が本文のどこにも現れない（3 種別・供給あり/なし）。
    [Theory]
    [InlineData(ReportKind.Daily)]
    [InlineData(ReportKind.Weekly)]
    [InlineData(ReportKind.Monthly)]
    public void 本文に実現損益の税引前費用前という呼称が現れない(ReportKind kind)
    {
        foreach (var review in new[] { Review(2_000m, 0.05m), Review(-2_000m, null) })
        {
            var md = ReportRenderer.RenderMarkdown(Weekly(review) with { Kind = kind });

            md.Should().NotContain("税引前・費用前");
            md.Should().NotContain("実現損益〔税引前");
            md.Should().NotContain("実現損益（税引前");
        }
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

        section.Should().Contain("損益に対する費用率: **算出不能**（分母となる約定代金差額が -2,000.00 USD で、0 以下です）");
        section.Should().Contain("**0% ではありません。**");
    }

    [Fact]
    public void 費用率の分母が約定代金差額であることを凡例で明示する()
    {
        var md = ReportRenderer.RenderMarkdown(Weekly(Review(2_000m, 0.05m)));

        md.Should().Contain("費用率の**分母は約定代金差額**（売却代金 − 取得代金。費用・税をいずれも控除しない値）です。");
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
