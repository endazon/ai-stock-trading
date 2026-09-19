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

    /// <summary>
    /// §2-b の実現損益の標識。**計算できない**ことを表す（04_report-templates 日報 §2-b は値を常に `不明` と定める）。
    /// 🔴 <see cref="Unsupplied"/>（記録源が無い）とは**別の語**である。混ぜない。
    /// </summary>
    private const string UnknownPnl = "不明";

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

        // 🔴 §2-b は §2 の直後・取引詳細の**前**である（計画テンプレートの節順。ADR-0030「節番号と並び順は計画が正」）。
        AppendDriftAdoptions(sb, view);

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

    // FR-06, FR-11, FR-16, ADR-0041 決定 1, #870, #859, IADR-0360 決定 3,
    // 04_report-templates 日報 §2-b「手動売買（損益不明）」。
    //
    // 🔴 **実現損益の列を置き、値は常に `不明`。** 列ごと落とさず、空欄にもしない
    //（列が無いと「不明」と書く場所が無く、空欄は 0 円の取引と読める）。**§1 の合計へ算入しない。**
    // 🔴 **`不明`（計算できない）と `未供給`（記録源が無い）と「該当なし」を混ぜない**（計画 §共通仕様 の語彙分割）。
    private static void AppendDriftAdoptions(StringBuilder sb, TradeHistoryView view)
    {
        sb.Append("### 2-b. 手動売買（損益不明）\n\n");

        if (view.DriftAdoptions is not { } adoptions)
        {
            sb.Append("- **手動売買の取り込みを照会できませんでした（供給元がありません）**: "
                + "「該当なし」とは区別しています。**0 件ではありません。**\n\n");
            return;
        }

        sb.Append("利用者が証券会社のアプリから直接売買した結果を、取引台帳へ取り込んだ記録です"
            + "（取引記録の**由来**が「手動売買による取り込み」の行）。\n\n");

        if (adoptions.Count == 0)
        {
            sb.Append("（該当なし）\n\n");
            return;
        }

        sb.Append("| # | 取り込み日時 | 市場 | 銘柄 | 取り込み前の数量 | 観測された数量 | 実現損益 | 操作者 | 理由 | 観測時刻 |\n");
        sb.Append("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");
        var index = 0;
        foreach (var a in adoptions.OrderBy(a => a.AdoptedAt).ThenBy(a => a.AdoptionId))
        {
            index++;
            sb.Append(CultureInfo.InvariantCulture,
                $"| {index} | {At(a.AdoptedAt)} | {MarketLabel(a.Market)} | {Cell(a.Symbol)} {Unsupplied} "
                + $"| {SignedQuantity(a.LedgerQuantityBefore)} | {SignedQuantity(a.BrokerQuantity)} | **{UnknownPnl}** "
                + $"| {TextOrUnsupplied(a.Actor)} | {TextOrUnsupplied(a.Reason)} | {At(a.ObservedAt)} |\n");
        }

        sb.Append('\n');
        // 標識は定数のため、補間ではなく定数畳み込みで組む（`Append(CultureInfo, ...)` の補間ハンドラは
        // 非補間の文字列リテラルと `+` で混ぜられない）。
        sb.Append("- 🔴 **実現損益は `" + UnknownPnl + "` です**（0 円ではありません）。"
            + "システム外の売買の**約定価格が分からない**ため、実現損益を計算できません。"
            + "**本欄の件は §1 の実現損益の合計へ算入していません**"
            + "（§1 の合計は「システムが約定させた取引の実現損益」です）。\n");
        sb.Append("- `" + UnknownPnl + "` は**計算できない**ことを、`" + Unsupplied + "` は**記録源が無い**ことを表します。"
            + "**「該当なし」「0」とは区別しています。**\n");
        sb.Append("- 日時は **JST**。取り込みは**数量だけ**を台帳へ反映しており、"
            + "建玉はその時点の平均取得単価で減っています（取得単価・残りの建玉の評価は変わりません）。\n");
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

    // §2-b の日時（JST・分まで）。テンプレートの `<YYYY-MM-DD HH:MM>` に合わせる。
    private static string At(DateTimeOffset instant) =>
        instant.ToOffset(ReportSchedule.JstOffset).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

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

    // §2-b の数量（**符号付き在庫**のため負もあり得る。ショート建玉の取り込みで符号が落ちると方向が読めない）。
    private static string SignedQuantity(int value) =>
        value.ToString("#,##0;-#,##0;0", CultureInfo.InvariantCulture);

    // 税（未供給は 0 と書かない）。
    private static string NumOrUnsupplied(decimal? value) => value is { } v ? Num(v) : Unsupplied;

    private static string TextOrUnsupplied(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Unsupplied : Cell(value);

    // 実現損益（符号付き・千区切り。既存 ReportRenderer の Yen と同形式）。
    private static string Signed(decimal value) => value.ToString("+#,##0;-#,##0;0", CultureInfo.InvariantCulture);
}
