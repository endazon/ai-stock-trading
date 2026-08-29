using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-16, #563, IADR-0269, 04_report-templates 日報 §2 / §3:
// **出口（`ReportRenderer` の本文）で** 取引履歴・取引詳細・見送り判断・ポジション一覧が出ることを固定する。
//
// 🔴 **これが #563 の受け入れ基準 4「呼ばれていないと落ちるテスト」である。**
// `TradeHistoryRendererTests` はレンダラを直接叩くため、**本番から呼ばれていなくても緑になる**——
// 実際、結線が 1 件も無いまま長期間その状態だった。**「呼ばれたこと」と「結果が出口へ出たこと」は別の事実**
// であるため、ここでは呼び出しの有無を見ず、**出口の本文**だけを見る。
// 全文の固定は `ReportTemplateGoldenTests`（daily-supplied.md / daily-unsupplied.md）が担う。
public class ReportRendererTradeHistoryTests
{
    private static ReportView Daily(TradeHistoryView? history, IReadOnlyList<ReportPosition>? positions) => new()
    {
        Kind = ReportKind.Daily,
        PeriodKey = "daily-2026-08-28",
        PeriodLabel = "2026-08-28",
        Markets = ["JP"],
        AssumptionsVersion = 3,
        Pnl = new PnlSummary(0m, 0m, 0m, 0m, 0m, 0, 0, 0),
        Narrative = "散文。",
        PolicySummary = "方針。",
        TradeHistory = history,
        Positions = positions,
    };

    private static TradeHistoryView History() => new()
    {
        Lines =
        [
            new TradeHistoryLine(1, new TimeOnly(9, 5), Market.Japan, "7203", SymbolName: null, TradeSide.Buy,
                Quantity: 100, FillPrice: 2_500m, Cost: 120m, Tax: null, RealizedPnl: 0m,
                Trigger: null, RationaleSummary: "始値が支持線で反発。"),
        ],
    };

    private static ReportPosition Position() =>
        new(Market.Japan, "7203", TradeSide.Buy, 100, 2_500m, 2_375m,
            CurrentPrice: 2_560m, UnrealizedPnl: 6_000m, BorrowFeeTotal: null, HoldingDays: null);

    // 受け入れ基準 1: §2 / 取引詳細 / 見送り判断 / §3 が**本文に出る**。
    [Fact]
    public void 日報の本文に取引履歴と取引詳細と見送り判断とポジション一覧が出る()
    {
        var md = ReportRenderer.RenderMarkdown(Daily(History(), [Position()]));

        md.Should().Contain("## 2. 取引履歴（全明細）");
        md.Should().Contain("### 取引詳細（選定・売買の判断理由）");
        md.Should().Contain("### 見送り判断（主要なもの）");
        md.Should().Contain("## 3. ポジション一覧（当日終了時点）");
    }

    // 供給した明細の中身が**そのまま出口へ届く**こと（節の見出しだけ出ていても結線とは言えない）。
    [Fact]
    public void 供給した明細の行と建玉の行が本文へ届く()
    {
        var md = ReportRenderer.RenderMarkdown(Daily(History(), [Position()]));

        md.Should().Contain("| 1 | 09:05 | JP | 7203 **未供給** | 買 | 100 | 2,500 | 120 | **未供給** | 0 | **未供給** | 始値が支持線で反発。 |");
        md.Should().Contain("| JP | 7203 | ロング | 100 | 2,500 | 2,560 | +6,000 | 2,375 | — | **未供給** |");
    }

    // 受け入れ基準 3: 約定が 0 件の日でも**節ごと消えない**。
    [Fact]
    public void 約定が0件の日でも節は消えず当日の約定なしと出る()
    {
        var md = ReportRenderer.RenderMarkdown(Daily(new TradeHistoryView(), []));

        md.Should().Contain("## 2. 取引履歴（全明細）");
        md.Should().Contain("（当日の約定なし）");
        md.Should().Contain("### 取引詳細（選定・売買の判断理由）");
        md.Should().Contain("### 見送り判断（主要なもの）");
        md.Should().Contain("## 3. ポジション一覧（当日終了時点）");
        md.Should().Contain("（当日終了時点の建玉なし）");
    }

    // 🔴 **肯定形**: 未供給（null）は「照会できませんでした」と明示する。
    [Fact]
    public void 明細と建玉が未供給なら照会できなかったことを明示する()
    {
        var md = ReportRenderer.RenderMarkdown(Daily(null, null));

        md.Should().Contain("**取引履歴を照会できませんでした（供給元がありません）**");
        md.Should().Contain("**建玉を照会できませんでした（供給元がありません）**");
    }

    // 🔴 **否定形（上の肯定形と対）**: 未供給を「約定なし」「建玉なし」へ倒さない。
    [Fact]
    public void 明細と建玉の未供給を約定なしや建玉なしと書かない()
    {
        var md = ReportRenderer.RenderMarkdown(Daily(null, null));

        md.Should().NotContain("（当日の約定なし）");
        md.Should().NotContain("（当日終了時点の建玉なし）");
        // 節そのものは消さない（#563 の再発は「節が消えていた」ことである）。
        md.Should().Contain("## 2. 取引履歴（全明細）");
        md.Should().Contain("## 3. ポジション一覧（当日終了時点）");
    }

    // 週報・月報は計画の粒度対応表が明細ではなく集計を求めている（節を勝手に増やさない）。
    [Theory]
    [InlineData(ReportKind.Weekly)]
    [InlineData(ReportKind.Monthly)]
    public void 週報と月報には明細とポジション一覧を出さない(ReportKind kind)
    {
        var view = Daily(History(), [Position()]) with
        {
            Kind = kind,
            PeriodKey = kind == ReportKind.Weekly ? "weekly-2026-W35" : "monthly-2026-08",
            PeriodLabel = kind == ReportKind.Weekly ? "2026-W35" : "2026-08",
        };

        var md = ReportRenderer.RenderMarkdown(view);

        md.Should().NotContain("## 2. 取引履歴（全明細）");
        md.Should().NotContain("## 3. ポジション一覧（当日終了時点）");
        // 週報・月報の見出し番号は動かしていない（対の肯定形）。
        md.Should().Contain(kind == ReportKind.Weekly ? "## 2. 振り返りと評価" : "## 2. 総括と評価");
    }

    // 日報の節番号は計画 04_report-templates の並び（§2 取引履歴・§3 ポジション・§4 リスク統制）に寄せる。
    [Fact]
    public void 日報の節は計画の並びで昇順に出る()
    {
        var md = ReportRenderer.RenderMarkdown(Daily(History(), [Position()]));

        var order = new[]
        {
            "## 1. 当日サマリ",
            "## 2. 取引履歴（全明細）",
            "## 3. ポジション一覧（当日終了時点）",
            "## 4. リスク統制の記録",
            "## 5. 市況・振り返り",
            "## 6. 翌営業日の方針",
        };

        var indexes = new List<int>();
        foreach (var heading in order)
        {
            var at = md.IndexOf(heading, StringComparison.Ordinal);
            // 🔴 **-1 を許さない。** IndexOf は見つからないと -1 を返すため、実在を先に主張しないと
            // 「節が消えた」状態で順序の検査が通ってしまう。
            at.Should().BeGreaterThanOrEqualTo(0, $"{heading} が本文に無い");
            indexes.Add(at);
        }

        indexes.Should().BeInAscendingOrder();
    }
}
