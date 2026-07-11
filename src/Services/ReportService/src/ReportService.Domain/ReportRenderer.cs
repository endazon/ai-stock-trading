using System.Globalization;
using System.Text;

namespace AiStockTrading.Report.Domain;

// FR-06/07/16, 04_report-templates, IADR-0032: 報告書テンプレートへの組み立て（純関数）。
// 数値はすべて PnlSummary（コード集計値）から埋め、散文（Narrative）は LLM ドラフトを挿入する（数値は LLM に計算させない）。
// 本スライスは日報の frontmatter＋当日サマリ＋散文＋翌営業日方針に限定する（明細/ポジション/リスク統制・週報/月報は後続）。
public static class ReportRenderer
{
    public static string RenderDailyMarkdown(DailyReportView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var date = view.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var markets = view.Markets.Count > 0 ? string.Join(", ", view.Markets) : "-";
        var status = view.ConfirmedAt is null ? "draft" : "fixed";
        var confirmedAt = view.ConfirmedAt?.ToString("o", CultureInfo.InvariantCulture) ?? "null";
        var p = view.Pnl;

        var sb = new StringBuilder();

        // YAML フロントマター（ナレッジベース保存時の検索・集計キー・04_report-templates 共通仕様）。
        sb.Append("---\n");
        sb.Append("report_type: daily\n");
        sb.Append(CultureInfo.InvariantCulture, $"period: {date}\n");
        sb.Append(CultureInfo.InvariantCulture, $"status: {status}\n");
        sb.Append(CultureInfo.InvariantCulture, $"based_on: {view.BasedOn ?? "null"}\n");
        sb.Append(CultureInfo.InvariantCulture, $"assumptions_version: v{view.AssumptionsVersion}\n");
        sb.Append(CultureInfo.InvariantCulture, $"confirmed_at: {confirmedAt}\n");
        sb.Append(CultureInfo.InvariantCulture, $"markets: [{markets}]\n");
        sb.Append("---\n\n");

        sb.Append(CultureInfo.InvariantCulture, $"# 日報 {date}\n\n");

        // 1. 当日サマリ（数値はコード集計値・FR-16）。
        sb.Append("## 1. 当日サマリ\n\n");
        sb.Append("| 項目 | 値 |\n");
        sb.Append("| --- | --- |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| 実現損益（税引後・費用込み） | {Yen(p.RealizedPnlNet)} |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| 評価損益（税引前・参考） | {Yen(p.UnrealizedPnl)} |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| 取引回数（買/売/決済） | {view.BuyCount} / {view.SellCount} / {p.RealizingTradeCount} |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| 費用合計（手数料・諸費用・為替） | {Yen(p.TotalCost)} |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| 源泉徴収税額 | {Yen(p.TaxWithheld)} |\n\n");

        // 2. 市況・振り返り（LLM ドラフトの散文）。数値は含めない。
        sb.Append("## 2. 市況・振り返り\n\n");
        sb.Append(string.IsNullOrWhiteSpace(view.Narrative) ? "（散文ドラフトなし）" : view.Narrative.Trim());
        sb.Append("\n\n");

        // 3. 翌営業日の方針（確定で取引方針として有効化される）。
        sb.Append("## 3. 翌営業日の方針\n\n");
        sb.Append(string.IsNullOrWhiteSpace(view.PolicySummary) ? "（方針未設定）" : view.PolicySummary.Trim());
        sb.Append('\n');

        return sb.ToString();
    }

    // 円建て表記（符号付き・千区切り）。実現/評価損益は符号を明示する。
    private static string Yen(decimal amount) =>
        amount.ToString("+#,##0;-#,##0;0", CultureInfo.InvariantCulture) + " 円";
}
