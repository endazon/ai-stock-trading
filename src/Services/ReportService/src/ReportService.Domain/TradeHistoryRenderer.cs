using System.Globalization;
using System.Text;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Report.Domain;

// FR-16, 04_report-templates 日報 §2, IADR-0038: 取引履歴（全明細）＋取引詳細＋見送り判断のレンダリング（純関数・決定的）。
// 数値は #63 台帳のコード集計値を提示するだけで再計算しない（LLM に計算させない・IADR-0032 と同方針）。実データ連携は後続。
public static class TradeHistoryRenderer
{
    public static string RenderMarkdown(TradeHistoryView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var sb = new StringBuilder();

        // §2 取引履歴（全明細）。1 約定＝1 行（04_report-templates の表定義に一致）。
        sb.Append("## 2. 取引履歴（全明細）\n\n");
        if (view.Lines.Count == 0)
        {
            sb.Append("（当日の約定なし）\n\n");
        }
        else
        {
            sb.Append("| # | 時刻 | 市場 | 銘柄 | 売買 | 数量 | 約定単価 | 手数料・費用 | 税 | 実現損益 | トリガー | 判断根拠（要約） |\n");
            sb.Append("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");
            foreach (var l in view.Lines)
            {
                sb.Append(CultureInfo.InvariantCulture,
                    $"| {l.Index} | {Time(l.Time)} | {MarketLabel(l.Market)} | {l.SymbolCode} {l.SymbolName} | {SideLabel(l.Side)} | {Num(l.Quantity)} | {Num(l.FillPrice)} | {Num(l.Cost)} | {Num(l.Tax)} | {Signed(l.RealizedPnl)} | {TriggerLabel(l.Trigger)} | {l.RationaleSummary} |\n");
            }

            sb.Append('\n');
        }

        // 取引詳細（選定・売買の判断理由）。1 取引＝1 ブロック（04_report-templates の形式）。
        if (view.Details.Count > 0)
        {
            sb.Append("### 取引詳細（選定・売買の判断理由）\n\n");
            foreach (var d in view.Details)
            {
                sb.Append(CultureInfo.InvariantCulture, $"#### #{d.Index} {Time(d.Time)} {d.SymbolLabel} {SideLabel(d.Side)}\n");
                sb.Append(CultureInfo.InvariantCulture, $"- **銘柄選定の理由**: {d.SelectionReason}\n");
                sb.Append(CultureInfo.InvariantCulture, $"- **売買判断の理由**: {d.DecisionReason}\n");
                sb.Append(CultureInfo.InvariantCulture, $"- **参照した情報**: {d.ReferencedInfo}\n");
                sb.Append(CultureInfo.InvariantCulture, $"- **想定シナリオ**: {d.Scenario}\n");
                sb.Append(CultureInfo.InvariantCulture, $"- **結果と評価**: {d.ResultEvaluation}\n\n");
            }
        }

        // 見送り判断（主要なもの）。
        sb.Append("### 見送り判断（主要なもの）\n\n");
        if (view.Skipped.Count == 0)
        {
            sb.Append("（見送りなし）\n");
        }
        else
        {
            foreach (var s in view.Skipped)
                sb.Append(CultureInfo.InvariantCulture, $"- {Time(s.Time)} {s.Symbol}: {s.Reason}\n");
        }

        return sb.ToString();
    }

    private static string Time(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    // 市場表記（04_report-templates・既存 ReportRenderer と整合: JP / US）。
    private static string MarketLabel(Market market) => market switch
    {
        Market.Japan => "JP",
        Market.UnitedStates => "US",
        _ => market.ToString(),
    };

    private static string SideLabel(TradeSide side) => side == TradeSide.Buy ? "買" : "売";

    private static string TriggerLabel(TradeTrigger trigger) => trigger switch
    {
        TradeTrigger.PriceMovement => "変動",
        TradeTrigger.StopLoss => "損切り",
        _ => "定時",
    };

    // 数量・約定単価・費用・税（非符号・千区切り）。
    private static string Num(decimal value) => value.ToString("#,##0", CultureInfo.InvariantCulture);

    // 実現損益（符号付き・千区切り。既存 ReportRenderer の Yen と同形式）。
    private static string Signed(decimal value) => value.ToString("+#,##0;-#,##0;0", CultureInfo.InvariantCulture);
}
