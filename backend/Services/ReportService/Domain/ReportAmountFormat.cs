using System.Globalization;
using AiStockTrading.Shared.Contracts.Trading;

namespace ReportService.Domain;

// FR-16, 04_report-templates, IADR-0116: 報告書で用いる金額表記（純関数）。
// 本文テンプレート（ReportRenderer）と Discord 要約（ReportSummary）で同じ表記を使うため、
// 書式を 1 箇所に置く（同じ数値が経路によって違って見えるのを防ぐ）。
//
// #364, IADR-0152 決定6: 集計値は**基準通貨**建てである（LedgerFill.PriceInBase と同じ規則）。
// 単位を「円」で直書きすると、基準通貨が USD になった瞬間に**ドルの数値に円の単位が付く**——統制・監査の
// 記録で単位を偽ることになるため、単位は MarketCurrency.Base から導く。
// 計画 §3 が定める**表示通貨 JPY への換算表示**（外貨併記・為替差損益の独立表示）は本書式の責務ではなく、
// 報告サイクル（#338）の範囲である。
public static class ReportAmountFormat
{
    /// <summary>
    /// 基準通貨建て表記（符号付き・千区切り・文化非依存・単位付き）。実現/評価損益は符号を明示する。
    /// 小数桁は通貨の補助単位に合わせる（USD はセント 2 桁・JPY は 0 桁）。
    /// </summary>
    public static string Base(decimal amount) => Of(amount, MarketCurrency.Base);

    /// <summary>
    /// #338, 04_report-templates §数値の定義: <b>表示通貨（円）</b>建ての表記。
    /// <para>
    /// 為替差損益と LLM 費用は<b>円建ての値である</b>——前者は「円換算により生じた損益」（定義上、円でしか表せない）、
    /// 後者は月次上限 15,000 円と同じ単位で読ませる必要がある。<b>基準通貨（USD）の書式を流用しない</b>——
    /// 単位を偽ると統制・費用の記録が読めなくなる（<see cref="Base"/> のコメントと同じ理由の裏返しである）。
    /// </para>
    /// </summary>
    public static string Jpy(decimal amount) => Of(amount, Currency.Jpy);

    /// <summary>
    /// 符号を付けない金額表記（<b>上限値・閾値など「損益ではない量」</b>に用いる）。
    /// <para>
    /// 損益に符号を明示するのと同じ理由で、<b>上限に符号を付けない</b>——
    /// 「+15,000 円の上限」は増減のある値のように読め、統制の閾値として読みにくい。
    /// </para>
    /// </summary>
    public static string Threshold(decimal amount, Currency currency)
    {
        var digits = CurrencyFormat.MinorUnitDigits(currency);
        var fraction = digits > 0 ? "." + new string('0', digits) : string.Empty;
        return amount.ToString($"#,##0{fraction}", CultureInfo.InvariantCulture)
            + " " + CurrencyFormat.CodeOf(currency);
    }

    /// <summary>指定通貨建ての表記（符号付き・千区切り・文化非依存・単位付き）。</summary>
    public static string Of(decimal amount, Currency currency)
    {
        var digits = CurrencyFormat.MinorUnitDigits(currency);
        var fraction = digits > 0 ? "." + new string('0', digits) : string.Empty;
        var format = $"+#,##0{fraction};-#,##0{fraction};0{fraction}";
        return amount.ToString(format, CultureInfo.InvariantCulture)
            + " " + CurrencyFormat.CodeOf(currency);
    }
}
