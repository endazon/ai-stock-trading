using System.Globalization;
using System.Text;

namespace AiStockTrading.Report.Domain;

// FR-06/07/16, 04_report-templates, IADR-0032: 報告書テンプレートへの組み立て（純関数・日報/週報/月報）。
// 数値はすべて PnlSummary（コード集計値）から埋め、散文（Narrative）は LLM ドラフトを挿入する（数値は LLM に計算させない）。
// 本スライスは frontmatter＋サマリ（数値・週報/月報は勝率）＋散文＋翌期間方針に限定する（明細/推移/内訳・データ依存節は後続）。
public static class ReportRenderer
{
    public static string RenderMarkdown(ReportView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var (kanji, summaryHeading, narrativeHeading, policyHeading) = Labels(view.Kind);
        var reportType = view.Kind.ToString().ToLowerInvariant();
        var markets = view.Markets.Count > 0 ? string.Join(", ", view.Markets) : "-";
        var status = view.ConfirmedAt is null ? "draft" : "fixed";
        var confirmedAt = view.ConfirmedAt?.ToString("o", CultureInfo.InvariantCulture) ?? "null";

        var sb = new StringBuilder();

        // YAML フロントマター（ナレッジベース保存時の検索・集計キー・04_report-templates 共通仕様）。
        sb.Append("---\n");
        sb.Append(CultureInfo.InvariantCulture, $"report_type: {reportType}\n");
        sb.Append(CultureInfo.InvariantCulture, $"period: {view.PeriodLabel}\n");
        sb.Append(CultureInfo.InvariantCulture, $"status: {status}\n");
        sb.Append(CultureInfo.InvariantCulture, $"based_on: {view.BasedOn ?? "null"}\n");
        sb.Append(CultureInfo.InvariantCulture, $"assumptions_version: v{view.AssumptionsVersion}\n");
        sb.Append(CultureInfo.InvariantCulture, $"confirmed_at: {confirmedAt}\n");
        sb.Append(CultureInfo.InvariantCulture, $"markets: [{markets}]\n");
        sb.Append("---\n\n");

        sb.Append(CultureInfo.InvariantCulture, $"# {kanji} {view.PeriodLabel}\n\n");

        // 1. サマリ（数値はコード集計値・FR-16）。
        sb.Append(CultureInfo.InvariantCulture, $"{summaryHeading}\n\n");
        sb.Append("| 項目 | 値 |\n");
        sb.Append("| --- | --- |\n");
        foreach (var (label, value) in SummaryRows(view))
            sb.Append(CultureInfo.InvariantCulture, $"| {label} | {value} |\n");
        sb.Append('\n');

        // 2. 散文（LLM ドラフト）。数値は含めない。
        sb.Append(CultureInfo.InvariantCulture, $"{narrativeHeading}\n\n");
        sb.Append(string.IsNullOrWhiteSpace(view.Narrative) ? "（散文ドラフトなし）" : view.Narrative.Trim());
        sb.Append("\n\n");

        // 3. 翌期間の方針（確定で取引方針として有効化される）。
        sb.Append(CultureInfo.InvariantCulture, $"{policyHeading}\n\n");
        sb.Append(string.IsNullOrWhiteSpace(view.PolicySummary) ? "（方針未設定）" : view.PolicySummary.Trim());
        sb.Append('\n');

        return sb.ToString();
    }

    // 種別ごとの見出し（漢字名・サマリ/散文/方針の各見出し）。
    private static (string Kanji, string Summary, string Narrative, string Policy) Labels(ReportKind kind) => kind switch
    {
        ReportKind.Weekly => ("週報", "## 1. 週間サマリ", "## 2. 振り返りと評価", "## 3. 翌週の方針"),
        ReportKind.Monthly => ("月報", "## 1. 月間サマリ", "## 2. 総括と評価", "## 3. 翌月の方針・投資方針"),
        _ => ("日報", "## 1. 当日サマリ", "## 2. 市況・振り返り", "## 3. 翌営業日の方針"),
    };

    // データ連携（#63 台帳/#12/#81・市場データ）が必要でこのスライスでは算出しない項目の表記。
    // テンプレートの「決まった形式」（04_report-templates・FR-16）の行構成は保ちつつ、値は後続連携で埋める。
    private const string Pending = "（データ連携後）";

    // 種別ごとのサマリ表の行（04_report-templates の各サマリ定義に一致）。数値は PnlSummary（コード集計値）から埋め、
    // データ依存の行（総資産・年初来・費用率・トリガー内訳・目標達成）は Pending プレースホルダで形式を保つ。
    // 取引回数（買/売/決済）は計画の「うち変動トリガー・損切り」内訳（#63 台帳連携待ち）の代替表記（仕様書に明記）。
    private static IEnumerable<(string Label, string Value)> SummaryRows(ReportView view)
    {
        var p = view.Pnl;
        var counts = $"{view.BuyCount} / {view.SellCount} / {p.RealizingTradeCount}";

        switch (view.Kind)
        {
            case ReportKind.Weekly:
                yield return ("週間実現損益（税引後・費用込み）", Yen(p.RealizedPnlNet));
                yield return ("勝率（勝ち取引/全決済取引）", WinRate(p));
                yield return ("取引回数（買/売/決済）", counts);
                yield return ("費用合計（手数料・諸費用・為替）", Yen(p.TotalCost));
                yield return ("週次目標に対する達成", Pending);
                break;

            case ReportKind.Monthly:
                yield return ("月間実現損益（税引後・費用込み）", Yen(p.RealizedPnlNet));
                yield return ("総資産（月初 → 月末）", Pending); // 04_report-templates の表記に一致（矢印前後に半角スペース）
                yield return ("年初来累計損益", Pending);
                yield return ("費用合計 / 費用率", $"{Yen(p.TotalCost)} / {Pending}");
                yield return ("月次目標に対する達成", Pending);
                break;

            default: // Daily
                yield return ("実現損益（税引後・費用込み）", Yen(p.RealizedPnlNet));
                yield return ("評価損益（税引前・参考）", Yen(p.UnrealizedPnl));
                yield return ("取引回数（買/売/決済）", counts);
                yield return ("費用合計（手数料・諸費用・為替）", Yen(p.TotalCost));
                yield return ("源泉徴収税額", Yen(p.TaxWithheld));
                yield return ("日次目標に対する達成", Pending);
                break;
        }
    }

    // 勝率（04_report-templates: 週報「<n%（n/n）>」形式）。決済ゼロなら "-（0/0）"。パーセントは文化非依存で整数表記する。
    private static string WinRate(PnlSummary p) =>
        p.RealizingTradeCount == 0
            ? "-（0/0）"
            : string.Format(CultureInfo.InvariantCulture, "{0:0}%（{1}/{2}）",
                (decimal)p.WinningTradeCount / p.RealizingTradeCount * 100m,
                p.WinningTradeCount, p.RealizingTradeCount);

    // 円建て表記（符号付き・千区切り）。書式は ReportAmountFormat に単一化する（IADR-0116: Discord 要約と同じ表記）。
    private static string Yen(decimal amount) => ReportAmountFormat.Yen(amount);
}
