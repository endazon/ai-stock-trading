using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-16, 04_report-templates 日報 §2, IADR-0042, #563, IADR-0269:
// 取引履歴（全明細）＋取引詳細＋見送り判断のレンダリング（純関数）を fake データで検証する。
//
// 🔴 **本ファイルだけでは #563 の再発を捕まえられない**——レンダラを直接叩くため、本番から呼ばれていなくても
// 緑になる。出口（`ReportRenderer` の全文ゴールデンと `ReportRendererTradeHistoryTests`）が結線を固定する。
public class TradeHistoryRendererTests
{
    // fake データ（全項目が供給された理想形）。買い（定時・実現損益0）と売り決済（損切り・実現益）を含む。
    private static TradeHistoryView Sample() => new()
    {
        Lines =
        [
            new TradeHistoryLine(1, new TimeOnly(9, 5), Market.Japan, "7203", "トヨタ", TradeSide.Buy,
                Quantity: 100, FillPrice: 2_500m, Cost: 120m, Tax: 0m, RealizedPnl: 0m,
                TradeTrigger.Scheduled, "日報方針の押し目買い条件に合致。詳細は #1"),
            new TradeHistoryLine(2, new TimeOnly(14, 30), Market.UnitedStates, "AAPL", "Apple", TradeSide.Sell,
                Quantity: 10, FillPrice: 31_500m, Cost: 200m, Tax: 380m, RealizedPnl: 1_520m,
                TradeTrigger.StopLoss, "損切りライン到達。詳細は #2"),
        ],
        Details =
        [
            new TradeDetailBlock(1, new TimeOnly(9, 5), "7203 トヨタ", TradeSide.Buy,
                SelectionReason: "監視銘柄。週報の重点戦略に対応。",
                DecisionReason: "始値が支持線で反発。出来高増。",
                ReferencedInfo: "KB: news/2026-07-10-toyota",
                Scenario: "目標 2,600 円・損切り 2,450 円・数日保有想定。",
                ResultEvaluation: "想定どおり支持線で約定。"),
        ],
        Skipped =
        [
            new SkippedDecision(new TimeOnly(10, 15), "6758 ソニー", "採算不足のため見送り。"),
        ],
    };

    // 現在の供給経路が実際に組み立てられる形（銘柄名・税・トリガーは記録源が無く、判断根拠は台帳から引く）。
    private static TradeHistoryView Supplied() => new()
    {
        Lines =
        [
            new TradeHistoryLine(1, new TimeOnly(9, 5), Market.Japan, "7203", SymbolName: null, TradeSide.Buy,
                Quantity: 100, FillPrice: 2_500m, Cost: 120m, Tax: null, RealizedPnl: 0m,
                Trigger: null, RationaleSummary: "始値が支持線で反発。出来高増。"),
            new TradeHistoryLine(2, new TimeOnly(14, 30), Market.UnitedStates, "AAPL", SymbolName: null, TradeSide.Sell,
                Quantity: 10, FillPrice: 315m, Cost: 20m, Tax: null, RealizedPnl: 1_520m,
                Trigger: null, RationaleSummary: null),
        ],
    };

    [Fact]
    public void 全明細の見出しと表ヘッダをテンプレートどおり生成する()
    {
        var md = TradeHistoryRenderer.RenderMarkdown(Sample());

        md.Should().Contain("## 2. 取引履歴（全明細）");
        md.Should().Contain("| # | 時刻 | 市場 | 銘柄 | 売買 | 数量 | 約定単価 | 手数料・費用 | 税 | 実現損益 | トリガー | 判断根拠（要約） |");
        md.Should().Contain("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
    }

    [Fact]
    public void 明細行を市場売買トリガーの表記と符号付き損益で埋める()
    {
        var md = TradeHistoryRenderer.RenderMarkdown(Sample());

        // 買い（定時・実現損益0）。市場 JP・売買 買・千区切り。
        md.Should().Contain("| 1 | 09:05 | JP | 7203 トヨタ | 買 | 100 | 2,500 | 120 | 0 | 0 | 定時 | 日報方針の押し目買い条件に合致。詳細は #1 |");
        // 売り決済（損切り・実現益）。市場 US・売買 売・実現損益は符号付き。
        md.Should().Contain("| 2 | 14:30 | US | AAPL Apple | 売 | 10 | 31,500 | 200 | 380 | +1,520 | 損切り | 損切りライン到達。詳細は #2 |");
    }

    [Fact]
    public void 取引詳細ブロックを選定売買判断の各項目で生成する()
    {
        var md = TradeHistoryRenderer.RenderMarkdown(Sample());

        md.Should().Contain("### 取引詳細（選定・売買の判断理由）");
        md.Should().Contain("#### #1 09:05 7203 トヨタ 買");
        md.Should().Contain("- **銘柄選定の理由**: 監視銘柄。週報の重点戦略に対応。");
        md.Should().Contain("- **売買判断の理由**: 始値が支持線で反発。出来高増。");
        md.Should().Contain("- **参照した情報**: KB: news/2026-07-10-toyota");
        md.Should().Contain("- **想定シナリオ**: 目標 2,600 円・損切り 2,450 円・数日保有想定。");
        md.Should().Contain("- **結果と評価**: 想定どおり支持線で約定。");
    }

    [Fact]
    public void 見送り判断を時刻銘柄理由で生成する()
    {
        var md = TradeHistoryRenderer.RenderMarkdown(Sample());

        md.Should().Contain("### 見送り判断（主要なもの）");
        md.Should().Contain("- 10:15 6758 ソニー: 採算不足のため見送り。");
    }

    [Fact]
    public void 表セルのパイプと改行を安全化し表崩れを防ぐ()
    {
        // 判断根拠へパイプ・改行が混ざっても表が崩れないこと（台帳の自由文をそのまま載せるため）。
        var view = new TradeHistoryView
        {
            Lines =
            [
                new TradeHistoryLine(1, new TimeOnly(9, 0), Market.Japan, "7203", "トヨタ", TradeSide.Buy,
                    Quantity: 100, FillPrice: 2_500m, Cost: 120m, Tax: 0m, RealizedPnl: 0m,
                    TradeTrigger.Scheduled, "支持線で反発 | 出来高増\n改行あり"),
            ],
        };

        var md = TradeHistoryRenderer.RenderMarkdown(view);

        md.Should().Contain("支持線で反発 \\| 出来高増 改行あり"); // パイプはエスケープ、改行は空白へ
        // 明細行の構造区切り（|）が崩れていない: エスケープ済み \| を除くと 12 列＝13 本の区切りパイプが残る。
        var row = md.Split('\n').First(line => line.StartsWith("| 1 |", StringComparison.Ordinal));
        row.Replace("\\|", "", StringComparison.Ordinal).Count(c => c == '|').Should().Be(13);
    }

    // 受け入れ基準 3（#563）: 約定が 0 件の日でも節ごと消えない。
    [Fact]
    public void 約定なしの日はプレースホルダで形式を保つ()
    {
        var md = TradeHistoryRenderer.RenderMarkdown(new TradeHistoryView());

        md.Should().Contain("## 2. 取引履歴（全明細）");
        md.Should().Contain("（当日の約定なし）");
        md.Should().NotContain("| # | 時刻"); // 空表ヘッダは出さない
        // 🔴 **見出しは供給の有無にかかわらず必ず出す**（#563: 節が丸ごと消えていたのが本件である）。
        md.Should().Contain("### 取引詳細（選定・売買の判断理由）");
        md.Should().Contain("### 見送り判断（主要なもの）");
    }

    // --- 未供給の表現（#563 受け入れ基準 2。**空欄と「該当なし」を混同しない**） ---

    // 🔴 **肯定形**: 記録源が無い列は `**未供給**` と描く。
    [Fact]
    public void 記録源が無い列を未供給と描く()
    {
        var md = TradeHistoryRenderer.RenderMarkdown(Supplied());

        // 銘柄名・税・トリガーは未供給、判断根拠は記録があるためそのまま出る。
        md.Should().Contain("| 1 | 09:05 | JP | 7203 **未供給** | 買 | 100 | 2,500 | 120 | **未供給** | 0 | **未供給** | 始値が支持線で反発。出来高増。 |");
        // 判断根拠を相関できなかった約定は、その行だけ未供給になる。
        md.Should().Contain("| 2 | 14:30 | US | AAPL **未供給** | 売 | 10 | 315 | 20 | **未供給** | +1,520 | **未供給** | **未供給** |");
    }

    // 🔴 **否定形（上の肯定形と対）**: 未供給を 0・「該当なし」へ倒さない。
    [Fact]
    public void 未供給の列を0や該当なしと書かない()
    {
        var md = TradeHistoryRenderer.RenderMarkdown(Supplied());

        var taxCell = md.Split('\n').First(l => l.StartsWith("| 1 |", StringComparison.Ordinal)).Split('|')[9].Trim();
        taxCell.Should().Be("**未供給**", "税は期間合計にのみ課され、約定単位へ配分できない");
        taxCell.Should().NotBe("0");

        var triggerCell = md.Split('\n').First(l => l.StartsWith("| 1 |", StringComparison.Ordinal)).Split('|')[11].Trim();
        triggerCell.Should().NotBe("定時", "起点が記録されていないことを「定時だった」と書かない");
        triggerCell.Should().NotBe("該当なし");
    }

    [Fact]
    public void 未供給の標識の意味を凡例で定義する()
    {
        var md = TradeHistoryRenderer.RenderMarkdown(Supplied());

        md.Should().Contain("`**未供給**` は**記録源が無い**ことを表します。**「該当なし」「0」とは区別しています。**");
        md.Should().Contain("時刻は **JST**");
        md.Should().Contain("**手数料・費用は前提条件からの概算**");
    }

    // 🔴 **肯定形**: 取引詳細の記録源が無いことを節の中で明示する。
    [Fact]
    public void 取引詳細の記録源が無いことを明示する()
    {
        var md = TradeHistoryRenderer.RenderMarkdown(Supplied());

        md.Should().Contain("### 取引詳細（選定・売買の判断理由）");
        md.Should().Contain("**取引詳細を照会できませんでした（供給元がありません）**");
    }

    // 🔴 **否定形（上の肯定形と対）**: 「該当なし」と書かない・空列の表現と混ぜない。
    [Fact]
    public void 取引詳細の未供給を該当なしと書かない()
    {
        var md = TradeHistoryRenderer.RenderMarkdown(Supplied());

        md.Should().NotContain("（該当する取引詳細なし）");
    }

    // 空列＝該当なし（未供給ではない）。上の 2 件と合わせて 3 状態を区別できることを固定する。
    [Fact]
    public void 取引詳細が空列なら該当なしと描き未供給とは書かない()
    {
        var md = TradeHistoryRenderer.RenderMarkdown(Supplied() with { Details = [] });

        md.Should().Contain("（該当する取引詳細なし）");
        md.Should().NotContain("**取引詳細を照会できませんでした（供給元がありません）**");
    }

    // 🔴 **肯定形**: 見送り判断の記録源が無いことを明示する。
    [Fact]
    public void 見送り判断の記録源が無いことを明示する()
    {
        var md = TradeHistoryRenderer.RenderMarkdown(Supplied());

        md.Should().Contain("### 見送り判断（主要なもの）");
        md.Should().Contain("**見送り判断を照会できませんでした（供給元がありません）**");
    }

    // 🔴 **否定形（上の肯定形と対）**: 「（見送りなし）」と書かない（取引機会を逸していない、と読める）。
    [Fact]
    public void 見送り判断の未供給を見送りなしと書かない()
    {
        var md = TradeHistoryRenderer.RenderMarkdown(Supplied());

        md.Should().NotContain("（見送りなし）");
    }

    [Fact]
    public void 見送り判断が空列なら見送りなしと描き未供給とは書かない()
    {
        var md = TradeHistoryRenderer.RenderMarkdown(Supplied() with { Skipped = [] });

        md.Should().Contain("（見送りなし）");
        md.Should().NotContain("**見送り判断を照会できませんでした（供給元がありません）**");
    }
}
