using System.Text.RegularExpressions;
using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-11, FR-16, ADR-0041 決定 1, #870, #859, IADR-0360 決定 3,
// 04_report-templates 日報 §2-b「手動売買（損益不明）」の描画を固定する。
//
// 🔴 計画の明文を 1 つずつ固定する ——「実現損益の列を置き、値は常に `不明`」「列ごと落とさない」「空欄にもしない」
// 「§1 の合計へ算入しない」「該当が無い日は『該当なし』と書く（欄ごと落とさない）」。
public class TradeHistoryRendererDriftAdoptionTests
{
    private static PeriodDriftAdoption Adoption(
        string symbol = "TSLA",
        Market market = Market.UnitedStates,
        TradeSide side = TradeSide.Sell,
        int quantity = 20,
        int before = 20,
        int after = 0,
        string actor = "owner",
        string reason = "moomoo アプリから直接売却したため台帳を合わせる。") =>
        new(new Guid("33333333-3333-3333-3333-333333333333"), symbol, market, side, quantity, before, after,
            new DateTimeOffset(2026, 9, 18, 5, 0, 0, TimeSpan.Zero),
            actor, reason,
            new DateTimeOffset(2026, 9, 18, 5, 30, 0, TimeSpan.Zero));

    private static string Render(IReadOnlyList<PeriodDriftAdoption>? adoptions) =>
        TradeHistoryRenderer.RenderMarkdown(new TradeHistoryView { Lines = [], DriftAdoptions = adoptions });

    [Fact]
    public void 取り込みがあれば_節_2b_の表に銘柄と数量と取り込み日時と操作者と理由が出る()
    {
        var md = Render([Adoption()]);

        md.Should().Contain("### 2-b. 手動売買（損益不明）");
        md.Should().Contain("| # | 取り込み日時 | 市場 | 銘柄 | 取り込み前の数量 | 観測された数量 | 実現損益 | 操作者 | 理由 | 観測時刻 |");
        // 日時は JST（UTC 05:30 → JST 14:30）。
        md.Should().Contain("| 1 | 2026-09-18 14:30 | US | TSLA");
        md.Should().Contain("| 20 | 0 |");
        md.Should().Contain("| owner |");
        md.Should().Contain("moomoo アプリから直接売却したため台帳を合わせる。");
        md.Should().Contain("2026-09-18 14:00 |", "観測時刻も JST で出す");
    }

    [Fact]
    public void 実現損益の列は常に不明であり_空欄にも_0_にもならない()
    {
        var md = Render([Adoption()]);

        var row = md.Split('\n').Single(l => l.StartsWith("| 1 | 2026-09-18 14:30", StringComparison.Ordinal));
        var cells = row.Split('|').Select(c => c.Trim()).ToList();

        // `| # | 取り込み日時 | 市場 | 銘柄 | 前 | 観測 | 実現損益 | 操作者 | 理由 | 観測時刻 |`
        // → 先頭・末尾の空要素を含めて 12 要素。実現損益は index 7。
        cells.Should().HaveCount(12, "🔴 列ごと落とさない（10 列）");
        cells[7].Should().Be("**不明**");
        cells[7].Should().NotBeEmpty("🔴 空欄は 0 円の取引と読める");
        cells[7].Should().NotContain("0");
    }

    [Fact]
    public void 凡例が不明と未供給を別の語として説明する()
    {
        var md = Render([Adoption()]);

        md.Should().Contain("**本欄の件は §1 の実現損益の合計へ算入していません**");
        md.Should().Contain("`不明` は**計算できない**ことを、`**未供給**` は**記録源が無い**ことを表します。");
    }

    [Fact]
    public void 取り込みが無い期間は該当なしと書き_欄ごと落とさない()
    {
        var md = Render([]);

        md.Should().Contain("### 2-b. 手動売買（損益不明）");
        md.Should().Contain("（該当なし）");
        md.Should().NotContain("照会できませんでした（供給元がありません）**: 「該当なし」");
    }

    [Fact]
    public void 照会できていない期間は該当なしと書かない()
    {
        var md = Render(null);

        md.Should().Contain("### 2-b. 手動売買（損益不明）");
        md.Should().Contain("**手動売買の取り込みを照会できませんでした（供給元がありません）**");
        // 🔴 「該当なし」と潰さない（0 件と読める）。
        md.Should().NotContain("（該当なし）");
    }

    [Fact]
    public void 節_2b_は_2_の直後かつ取引詳細の前に出る()
    {
        var md = Render([Adoption()]);

        var section2 = md.IndexOf("## 2. 取引履歴（全明細）", StringComparison.Ordinal);
        var section2b = md.IndexOf("### 2-b. 手動売買（損益不明）", StringComparison.Ordinal);
        var details = md.IndexOf("### 取引詳細（選定・売買の判断理由）", StringComparison.Ordinal);

        section2.Should().BeLessThan(section2b);
        section2b.Should().BeLessThan(details, "計画テンプレートの節順（ADR-0030: 節番号と並び順は計画が正）");
    }

    [Fact]
    public void 空売り建玉の取り込みは符号付きの数量で出る()
    {
        var md = Render([Adoption(side: TradeSide.Buy, quantity: 30, before: -50, after: -20)]);

        // 🔴 符号が落ちるとロングの決済に読める。
        md.Should().Contain("| -50 | -20 |");
    }

    [Fact]
    public void 理由と操作者の自由記述は表を壊さない()
    {
        var md = Render([Adoption(reason: "パイプ | を含む\n理由", actor: "owner|x")]);

        var row = md.Split('\n').Single(l => l.StartsWith("| 1 | ", StringComparison.Ordinal));

        // エスケープ済みのパイプを除いた区切りだけを数える。改行が畳まれていなければ行そのものが 2 本に割れる。
        Regex.Split(row, @"(?<!\\)\|").Should().HaveCount(12, "パイプはエスケープし改行は畳む");
    }
}
