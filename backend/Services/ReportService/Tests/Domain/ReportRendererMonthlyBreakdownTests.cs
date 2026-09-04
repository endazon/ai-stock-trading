using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-07, FR-16, #615, IADR-0306, 04_report-templates 月報 §2「週別・市場別の内訳」:
// **出口（`ReportRenderer` の本文）で** 3 表が出ることを固定する。
//
// 🔴 **純関数のテスト（PeriodBreakdownTests）だけでは、結線が無くても緑になる**（IADR-0269 決定1）。
// 全文の固定は `ReportTemplateGoldenTests`（monthly-supplied.md / monthly-unsupplied.md）が担う。
public class ReportRendererMonthlyBreakdownTests
{
    private static readonly DateTimeOffset Mon = new(2026, 8, 24, 9, 5, 0, TimeSpan.FromHours(9));

    private static ReportView Monthly(
        IReadOnlyList<FillPnlAttribution>? attributions, BorrowFeeRecord? borrowFees = null) => new()
        {
            Kind = ReportKind.Monthly,
            PeriodKey = "monthly-2026-08",
            PeriodLabel = "2026-08",
            Markets = ["JP", "US"],
            AssumptionsVersion = 3,
            Pnl = new PnlSummary(2_000m, 100m, 380m, 1_520m, 0m, 4, 2, 1),
            Narrative = "散文。",
            PolicySummary = "方針。",
            FillAttributions = attributions,
            BorrowFees = borrowFees,
        };

    private static FillPnlAttribution Entry(
        int sequence, int addDays, string symbol, Market market, TradeSide side,
        decimal realized, bool realizing) =>
        new(sequence, Mon.AddDays(addDays),
            DateOnly.FromDateTime(Mon.AddDays(addDays).DateTime), market, symbol, side,
            Quantity: 100, Price: 2_500m, Cost: 120m,
            RealizedPnlGross: realized, Realizing: realizing, Rationale: null);

    // 月内で 2 つの ISO 週にまたがる列（W35 建て → W37 決済）。
    private static IReadOnlyList<FillPnlAttribution> Month() =>
    [
        Entry(1, 0, "7203", Market.Japan, TradeSide.Buy, 0m, realizing: false),
        Entry(2, 0, "AAPL", Market.UnitedStates, TradeSide.Buy, 0m, realizing: false),
        Entry(3, 14, "AAPL", Market.UnitedStates, TradeSide.Sell, 250m, realizing: true),
        Entry(4, 15, "7203", Market.Japan, TradeSide.Sell, -2_000m, realizing: true),
    ];

    // --- 結線（出口に 3 表が出ること） ---

    [Fact]
    public void 月報の本文に週別と市場別と方向別の3表が出る()
    {
        var md = ReportRenderer.RenderMarkdown(Monthly(Month()));

        var section = Section(md, "## 2. 週別・市場別の内訳", "## 3. 税金レビュー");
        section.Should().Contain("| 週 | 実現損益 | 取引数 | 備考 |");
        section.Should().Contain("| 市場 | 実現損益 | 費用 | 主要銘柄（損益上位/下位） |");
        section.Should().Contain("| 建玉の方向 | 実現損益 | 取引数 | 勝率 | 費用（うち借株料） |");
        section.Should().NotContain("本節は未実装です");
    }

    // 🔴 ADR-0030 決定1・決定2: **既存の節番号を繰り上げない。**
    [Fact]
    public void 既存の節番号を繰り上げない()
    {
        var md = ReportRenderer.RenderMarkdown(Monthly(Month()));

        md.Should().Contain("## 1. 月間サマリ");
        md.Should().Contain("## 2. 週別・市場別の内訳");
        md.Should().Contain("## 3. 税金レビュー");
        md.Should().Contain("## 4. 総括と評価");
        md.Should().Contain("## 5. バックテスト / SIMULATE / 実弾の三者比較");
        md.Should().Contain("## 6. リスク統制と前提条件の見直し");
        md.Should().Contain("## 7. 当月の LLM 利用実績");
        md.Should().Contain("## 8. 翌月の方針・投資方針");
    }

    [Fact]
    public void 月報以外では週別市場別の内訳を出さない()
    {
        foreach (var kind in new[] { ReportKind.Daily, ReportKind.Weekly })
        {
            var md = ReportRenderer.RenderMarkdown(Monthly(Month()) with
            {
                Kind = kind,
                PeriodKey = kind == ReportKind.Daily ? "daily-2026-08-28" : "weekly-2026-W35",
                PeriodLabel = kind == ReportKind.Daily ? "2026-08-28" : "2026-W35",
            });

            md.Should().NotContain("## 2. 週別・市場別の内訳");
            md.Should().NotContain("| 建玉の方向 | 実現損益 | 取引数 | 勝率 | 費用（うち借株料） |");
        }
    }

    // --- 未供給・約定なし・決済なしの区別（潰さない） ---

    [Fact]
    public void 帰属が未供給なら約定なしと区別して節ごと出す()
    {
        var md = ReportRenderer.RenderMarkdown(Monthly(null));

        md.Should().Contain("## 2. 週別・市場別の内訳");
        md.Should().Contain("**内訳を組み立てられませんでした（供給元がありません）**");
        md.Should().NotContain("| 週 | 実現損益 | 取引数 | 備考 |");
    }

    [Fact]
    public void 約定が0件なら未供給と区別して約定なしと書く()
    {
        var section = Section(
            ReportRenderer.RenderMarkdown(Monthly([])), "## 2. 週別・市場別の内訳", "## 3. 税金レビュー");

        section.Should().Contain("（当月の約定なし）");
        section.Should().NotContain("供給元がありません");
    }

    // 🔴 **約定が 1 件も無い市場も行を出す**（計画が行を固定している）。0 を「収支 0」と読ませない。
    [Fact]
    public void 約定が無い市場も行を出し当月の約定なしと明記する()
    {
        var section = Section(
            ReportRenderer.RenderMarkdown(Monthly(
                [Entry(1, 0, "AAPL", Market.UnitedStates, TradeSide.Buy, 0m, realizing: false)])),
            "## 2. 週別・市場別の内訳", "## 3. 税金レビュー");

        section.Should().Contain("| 日本株 |");
        section.Should().Contain("| 米国株 |");
        section.Should().Contain("（当月の約定なし）");
        // 約定はあるが決済が無い市場は別の文言（どちらも「損益 0」ではない）。
        section.Should().Contain("（当月の決済なし＝新規建てのみ）");
        section.Should().Contain("**数値の 0 は「取引して収支が 0 だった」ではありません。**");
    }

    // --- 借株料（費用へ足さない・未供給と 0 を区別する） ---

    // 🔴 借株料を費用へ足すと、本節の費用の合計が §1 の費用合計と一致しなくなる。
    [Fact]
    public void 借株料は費用へ足さず別掲する()
    {
        var borrowFees = new BorrowFeeRecord(
            [new BorrowFeeAccrued("AAPL", Market.UnitedStates, new DateOnly(2026, 8, 3), 0.06m, 10_000m, 1.64m,
                new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero))],
            []);

        var section = Section(
            ReportRenderer.RenderMarkdown(Monthly(Month(), borrowFees)),
            "## 2. 週別・市場別の内訳", "## 3. 税金レビュー");

        section.Should().Contain("（借株料 +1.64 USD は別掲・§6.1）");
        // ロングには借株料が発生しない。
        section.Should().Contain("（借株料 —）");
        section.Should().Contain("**借株料は「費用」の列に含めていません**");
    }

    [Fact]
    public void 借株料が未供給なら0ではなく未供給と描く()
    {
        var section = Section(
            ReportRenderer.RenderMarkdown(Monthly(Month())),
            "## 2. 週別・市場別の内訳", "## 3. 税金レビュー");

        section.Should().Contain("（借株料 **未供給**）");
    }

    [Fact]
    public void 料率が取れず未計上だった件数を借株料の脇に出す()
    {
        var borrowFees = new BorrowFeeRecord(
            [new BorrowFeeAccrued("AAPL", Market.UnitedStates, new DateOnly(2026, 8, 3), 0.06m, 10_000m, 1.64m,
                new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero))],
            [new BorrowFeeAccrualUnavailable("TSLA", Market.UnitedStates, new DateOnly(2026, 8, 4), "料率照会に失敗",
                new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero))]);

        var section = Section(
            ReportRenderer.RenderMarkdown(Monthly(Month(), borrowFees)),
            "## 2. 週別・市場別の内訳", "## 3. 税金レビュー");

        section.Should().Contain("未計上 1 件あり");
    }

    // --- 内容 ---

    [Fact]
    public void 週別は約定のあった週だけをISO週ラベルの昇順で出す()
    {
        var section = Section(
            ReportRenderer.RenderMarkdown(Monthly(Month())), "## 2. 週別・市場別の内訳", "## 3. 税金レビュー");

        section.Should().Contain("| 2026-W35 |");
        section.Should().Contain("| 2026-W37 |");
        // 約定が 1 件も無い W36 は行そのものが無い。
        section.Should().NotContain("| 2026-W36 |");
        section.IndexOf("| 2026-W35 |", StringComparison.Ordinal)
            .Should().BeLessThan(section.IndexOf("| 2026-W37 |", StringComparison.Ordinal));
    }

    [Fact]
    public void 方向別の勝率は決済が無ければハイフンで出し0パーセントとは書かない()
    {
        var section = Section(
            ReportRenderer.RenderMarkdown(Monthly(
                [Entry(1, 0, "AAPL", Market.UnitedStates, TradeSide.Buy, 0m, realizing: false)])),
            "## 2. 週別・市場別の内訳", "## 3. 税金レビュー");

        section.Should().Contain("-（0/0）");
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
