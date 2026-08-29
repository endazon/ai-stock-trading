using System.Globalization;
using System.Text;
using AiStockTrading.Shared.Contracts.Trading;

namespace ReportService.Domain;

// FR-16, 04_report-templates 日報 §2, IADR-0042: 取引履歴（全明細）＋取引詳細＋見送り判断のレンダリング（純関数・決定的）。
// 数値は #63 台帳のコード集計値を提示するだけで再計算しない（LLM に計算させない・IADR-0032 と同方針）。
//
// 🔴 **#563 / IADR-0269: 本レンダラは `ReportRenderer` の日報本文から呼ばれる。**
// 以前は本番からの呼び出しが 1 件も無く、レンダラ単体テストだけが緑で、**節が本文に一度も出ていなかった**。
// 出口（`ReportRenderer` の全文ゴールデン）で固定してあるので、結線を外すとゴールデンが赤くなる。
//
// 🔴 **未供給（`null`）を 0・「該当なし」へ潰さない。** セルは `**未供給**`、節は「照会できませんでした」で表す
//（本サービスの既存の 2 系統に揃える。新しい表現を作らない）。
public static class TradeHistoryRenderer
{
    /// <summary>表のセルで「記録源が無い」ことを表す標識。**「該当なし」「0」とは別物である。**</summary>
    private const string Unsupplied = "**未供給**";

    public static string RenderMarkdown(TradeHistoryView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var sb = new StringBuilder();

        // §2 取引履歴（全明細）。1 約定＝1 行（04_report-templates の表定義に一致）。
        sb.Append("## 2. 取引履歴（全明細）\n\n");
        if (view.Lines.Count == 0)
        {
            // 🔴 **約定 0 件は未供給ではない**（§1 サマリの取引回数 0 と整合する事実）。節ごと消さない。
            sb.Append("（当日の約定なし）\n\n");
        }
        else
        {
            sb.Append("| # | 時刻 | 市場 | 銘柄 | 売買 | 数量 | 約定単価 | 手数料・費用 | 税 | 実現損益 | トリガー | 判断根拠（要約） |\n");
            sb.Append("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");
            foreach (var l in view.Lines)
            {
                // 自由記述（銘柄名・判断根拠）は Markdown 表セルとして安全化する（パイプ/改行で表が崩れるのを防ぐ）。
                sb.Append(CultureInfo.InvariantCulture,
                    $"| {l.Index} | {Time(l.Time)} | {MarketLabel(l.Market)} | {SymbolCell(l)} | {SideLabel(l.Side)} | {Num(l.Quantity)} | {Num(l.FillPrice)} | {Num(l.Cost)} | {NumOrUnsupplied(l.Tax)} | {Signed(l.RealizedPnl)} | {TriggerLabel(l.Trigger)} | {TextOrUnsupplied(l.RationaleSummary)} |\n");
            }

            sb.Append('\n');
            AppendLegend(sb);
        }

        AppendDetails(sb, view);
        AppendSkipped(sb, view);

        return sb.ToString();
    }

    // 表の読み方。**セルごとに長文を書くと 12 列 × N 行が読めなくなる**ため、標識の意味は 1 箇所で定義する。
    private static void AppendLegend(StringBuilder sb)
    {
        sb.Append("- 時刻は **JST**（報告期間の基準時刻）。**手数料・費用は前提条件からの概算**であり、"
            + "ブローカの請求実額ではありません。\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"- `{Unsupplied}` は**記録源が無い**ことを表します。**「該当なし」「0」とは区別しています。**\n");
        sb.Append("  - **銘柄名**: 台帳は銘柄コードのみを保持しています。\n");
        sb.Append("  - **税**: 源泉徴収税額は**期間合計にのみ**課され、約定単位へ配分する規則がありません"
            + "（合計は §1 サマリの「源泉徴収税額」を参照）。\n");
        sb.Append("  - **トリガー**: 判断の起点（定時 / 変動 / 損切り）が記録されていません。\n");
        sb.Append("  - **判断根拠（要約）**: 取引判断の記録を相関できなかった約定のみ。"
            + "**記録がある約定は、記録された根拠をそのまま転記しています**（報告書生成時に文章を作っていません）。\n");
        sb.Append('\n');
    }

    // 取引詳細（選定・売買の判断理由）。1 取引＝1 ブロック（04_report-templates の形式）。
    // **null＝供給元がない／空列＝該当する取引が無い。** どちらでも見出しは必ず出す（節ごと消さない）。
    private static void AppendDetails(StringBuilder sb, TradeHistoryView view)
    {
        sb.Append("### 取引詳細（選定・売買の判断理由）\n\n");

        if (view.Details is not { } details)
        {
            sb.Append("- **取引詳細を照会できませんでした（供給元がありません）**: "
                + "「該当する取引なし」とは区別しています。銘柄選定の理由・参照した情報・想定シナリオ・結果と評価を"
                + "**分けて持つ記録がまだありません**。**記録された判断根拠は §2 の「判断根拠（要約）」列に出しています。**\n\n");
            return;
        }

        if (details.Count == 0)
        {
            sb.Append("（該当する取引詳細なし）\n\n");
            return;
        }

        foreach (var d in details)
        {
            sb.Append(CultureInfo.InvariantCulture, $"#### #{d.Index} {Time(d.Time)} {d.SymbolLabel} {SideLabel(d.Side)}\n");
            sb.Append(CultureInfo.InvariantCulture, $"- **銘柄選定の理由**: {d.SelectionReason}\n");
            sb.Append(CultureInfo.InvariantCulture, $"- **売買判断の理由**: {d.DecisionReason}\n");
            sb.Append(CultureInfo.InvariantCulture, $"- **参照した情報**: {d.ReferencedInfo}\n");
            sb.Append(CultureInfo.InvariantCulture, $"- **想定シナリオ**: {d.Scenario}\n");
            sb.Append(CultureInfo.InvariantCulture, $"- **結果と評価**: {d.ResultEvaluation}\n\n");
        }
    }

    // 見送り判断（主要なもの）。**null＝供給元がない／空列＝見送りなし。**
    // 🔴 見送り（Hold）はイベント化されていないため、現状は常に null＝未供給である。
    // 「（見送りなし）」と書くと「取引機会を逸していない」と読めるため、混同しない。
    private static void AppendSkipped(StringBuilder sb, TradeHistoryView view)
    {
        sb.Append("### 見送り判断（主要なもの）\n\n");

        if (view.Skipped is not { } skipped)
        {
            sb.Append("- **見送り判断を照会できませんでした（供給元がありません）**: "
                + "「見送りなし」とは区別しています。**0 件ではありません。**\n");
            return;
        }

        if (skipped.Count == 0)
        {
            sb.Append("（見送りなし）\n");
            return;
        }

        foreach (var s in skipped)
            sb.Append(CultureInfo.InvariantCulture, $"- {Time(s.Time)} {s.Symbol}: {s.Reason}\n");
    }

    // 銘柄セル。**名称が未供給なら標識を添える**（コードだけを出すと「名称が無い銘柄」と読める）。
    private static string SymbolCell(TradeHistoryLine line) =>
        string.IsNullOrWhiteSpace(line.SymbolName)
            ? $"{Cell(line.SymbolCode)} {Unsupplied}"
            : $"{Cell(line.SymbolCode)} {Cell(line.SymbolName)}";

    // Markdown 表セルの安全化: パイプはエスケープし改行は空白へ畳む（表の区切り崩れ・行崩れを防ぐ）。
    private static string Cell(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");

    private static string Time(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    // 市場表記（04_report-templates・既存 ReportRenderer と整合: JP / US）。
    private static string MarketLabel(Market market) => market switch
    {
        Market.Japan => "JP",
        Market.UnitedStates => "US",
        _ => market.ToString(),
    };

    private static string SideLabel(TradeSide side) => side == TradeSide.Buy ? "買" : "売";

    // 🔴 **null（起点が記録されていない）を「定時」へ倒さない。**
    private static string TriggerLabel(TradeTrigger? trigger) => trigger switch
    {
        TradeTrigger.PriceMovement => "変動",
        TradeTrigger.StopLoss => "損切り",
        TradeTrigger.Scheduled => "定時",
        _ => Unsupplied,
    };

    // 数量・約定単価・費用（非符号・千区切り）。
    private static string Num(decimal value) => value.ToString("#,##0", CultureInfo.InvariantCulture);

    // 税（未供給は 0 と書かない）。
    private static string NumOrUnsupplied(decimal? value) => value is { } v ? Num(v) : Unsupplied;

    private static string TextOrUnsupplied(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Unsupplied : Cell(value);

    // 実現損益（符号付き・千区切り。既存 ReportRenderer の Yen と同形式）。
    private static string Signed(decimal value) => value.ToString("+#,##0;-#,##0;0", CultureInfo.InvariantCulture);
}
