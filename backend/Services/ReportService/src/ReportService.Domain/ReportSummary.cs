using System.Globalization;
using System.Text;

namespace AiStockTrading.Report.Domain;

// FR-06, FR-09, FR-16, 04_workflows/03_reporting-cycle, IADR-0116 決定4, #280:
// Discord へ提示する要約の組み立て（純関数・決定的）。
//
// 計画書のシーケンス「REP->>DC: ドラフト提示（要約＋閲覧リンク）」の要約にあたる。
// 数値は **コード集計値（PnlSummary）だけ**を使い、LLM に数値を語らせない（FR-16・IADR-0032 の踏襲）。
// 散文は LLM 出力のため、**この関数の内側で必ず**サニタイズを通す（呼び出し側が忘れられる形にしない）。
public static class ReportSummary
{
    /// <summary>要約全体の長さ上限。数値行は必ず残し、超過分は散文側を詰める。</summary>
    public const int MaxLength = ReportSummarySanitizer.DefaultMaxLength;

    public static string Build(ReportKind kind, string periodLabel, PnlSummary pnl, string? narrative)
    {
        ArgumentNullException.ThrowIfNull(pnl);

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{KindLabel(kind)} {periodLabel}（承認待ち）\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"実現損益（税引後・費用込み）: {ReportAmountFormat.Yen(pnl.RealizedPnlNet)}"
            + $" ／ 費用: {ReportAmountFormat.Yen(pnl.TotalCost)}"
            + $" ／ 取引: {pnl.TradeCount} 件（決済 {pnl.RealizingTradeCount}・勝ち {pnl.WinningTradeCount}）");

        // 散文は残り枠に収める（数値行が切り落とされないよう、上限は散文側に配分する）。
        var remaining = MaxLength - sb.Length - 2; // 区切りの空行ぶん
        var text = remaining > 0 ? ReportSummarySanitizer.Sanitize(narrative, remaining) : string.Empty;

        if (text.Length > 0)
            sb.Append(CultureInfo.InvariantCulture, $"\n\n{text}");

        return sb.ToString();
    }

    private static string KindLabel(ReportKind kind) => kind switch
    {
        ReportKind.Weekly => "週報",
        ReportKind.Monthly => "月報",
        _ => "日報",
    };
}
