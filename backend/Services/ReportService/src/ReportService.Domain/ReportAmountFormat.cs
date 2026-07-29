using System.Globalization;

namespace AiStockTrading.Report.Domain;

// FR-16, 04_report-templates, IADR-0116: 報告書で用いる金額表記（純関数）。
// 本文テンプレート（ReportRenderer）と Discord 要約（ReportSummary）で同じ表記を使うため、
// 書式を 1 箇所に置く（同じ数値が経路によって違って見えるのを防ぐ）。
public static class ReportAmountFormat
{
    /// <summary>円建て表記（符号付き・千区切り・文化非依存）。実現/評価損益は符号を明示する。</summary>
    public static string Yen(decimal amount) =>
        amount.ToString("+#,##0;-#,##0;0", CultureInfo.InvariantCulture) + " 円";
}
