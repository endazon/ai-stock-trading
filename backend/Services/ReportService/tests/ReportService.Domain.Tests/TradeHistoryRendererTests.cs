using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Report.Domain.Tests;

// FR-16, 04_report-templates 日報 §2, IADR-0042: 取引履歴（全明細）＋取引詳細＋見送り判断のレンダリング（純関数）を fake データで検証する。
public class TradeHistoryRendererTests
{
    // fake データ（#63 台帳の実約定連携は後続）。買い（定時・実現損益0）と売り決済（損切り・実現益）を含む。
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
        // 実 LLM 取引詳細文の結線後に判断根拠へパイプ・改行が混ざっても表が崩れないこと（後続結線に備えた安全化）。
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

    [Fact]
    public void 約定なしの日はプレースホルダで形式を保つ()
    {
        var md = TradeHistoryRenderer.RenderMarkdown(new TradeHistoryView());

        md.Should().Contain("## 2. 取引履歴（全明細）");
        md.Should().Contain("（当日の約定なし）");
        md.Should().NotContain("| # | 時刻"); // 空表ヘッダは出さない
        md.Should().Contain("### 見送り判断（主要なもの）");
        md.Should().Contain("（見送りなし）");
        // 取引詳細が無ければ見出しも出さない。
        md.Should().NotContain("### 取引詳細");
    }
}
