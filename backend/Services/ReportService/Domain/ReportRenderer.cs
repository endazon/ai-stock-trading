using System.Globalization;
using System.Text;
using AiStockTrading.Shared.Contracts.Trading;

namespace ReportService.Domain;

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
        // INDEX 決定43, #92, 04_report-templates 共通仕様: 報告書の機密区分は **`internal`** で確定している
        // （全報告書共通・`internal` × ZDR 有効の構成）。基盤のデータ越境ポリシーの判定入力になるため、
        // **書かない選択肢は無い**——欠けると受け手が既定区分で扱い、区分の決定が無かったことになる。
        sb.Append(CultureInfo.InvariantCulture, $"confidentiality: {ReportPolicyYaml.Confidentiality}\n");
        sb.Append("---\n\n");

        sb.Append(CultureInfo.InvariantCulture, $"# {kanji} {view.PeriodLabel}\n\n");

        // 1. サマリ（数値はコード集計値・FR-16）。
        sb.Append(CultureInfo.InvariantCulture, $"{summaryHeading}\n\n");
        sb.Append("| 項目 | 値 |\n");
        sb.Append("| --- | --- |\n");
        foreach (var (label, value) in SummaryRows(view))
            sb.Append(CultureInfo.InvariantCulture, $"| {label} | {value} |\n");
        sb.Append('\n');

        // 🔴 #563, IADR-0269: **日報だけ**、サマリの直後に §2 取引履歴（全明細）・取引詳細・見送り判断と
        // §3 ポジション一覧が入り、リスク統制の記録（§4）→ 散文（§5）→ 翌営業日の方針（§6）と続く。
        // 週報・月報は従来どおり 散文（§2）→ 方針（§3）→ リスク統制（§4）… の並びである
        //（計画の粒度対応表が、週報・月報には明細ではなく集計を求めているため）。
        if (view.Kind == ReportKind.Daily)
        {
            AppendTradeHistory(sb, view);
            AppendPositions(sb, view);
            AppendRiskControlRecords(sb, view);
            // リスク統制の記録の各節は末尾に空行を残さない（次節が "\n" を前置する契約）。
            // 散文はその契約を持たない見出しのため、ここで 1 行空ける。
            sb.Append('\n');
            AppendNarrative(sb, view, narrativeHeading);
            AppendPolicy(sb, view, policyHeading);
        }
        else
        {
            AppendNarrative(sb, view, narrativeHeading);
            AppendPolicy(sb, view, policyHeading);
            AppendRiskControlRecords(sb, view);
        }

        // 5. 稼働状況（OpenD）— 日報＝当日の稼働率と Stage 1 日数への算入可否 / 月報＝分布（INDEX 決定34）。
        AppendUptime(sb, view);
        // 6. 三者比較（月報のみ・04_report-templates 月報 §5）。
        AppendThreeWayComparison(sb, view);
        // 7. 当月の LLM 利用実績（月報のみ・同 §7。#282 で計上された費用の出口）。
        AppendLlmUsage(sb, view);

        return sb.ToString();
    }

    // 散文（LLM ドラフト）。数値は含めない。
    private static void AppendNarrative(StringBuilder sb, ReportView view, string heading)
    {
        sb.Append(CultureInfo.InvariantCulture, $"{heading}\n\n");
        sb.Append(string.IsNullOrWhiteSpace(view.Narrative) ? "（散文ドラフトなし）" : view.Narrative.Trim());
        sb.Append("\n\n");
    }

    // 翌期間の方針（確定で取引方針として有効化される）＋ INDEX 決定29, #338, IADR-0252 の **YAML ブロック併記**。
    // 表（人間可読）と YAML（機械可読）を併記し、**取引判断サービスは YAML だけを読む**。
    private static void AppendPolicy(StringBuilder sb, ReportView view, string heading)
    {
        sb.Append(CultureInfo.InvariantCulture, $"{heading}\n\n");
        sb.Append(string.IsNullOrWhiteSpace(view.PolicySummary) ? "（方針未設定）" : view.PolicySummary.Trim());
        sb.Append("\n\n");
        sb.Append(ReportPolicyYaml.Render(view));
    }

    // リスク統制の記録（維持率割れによる自動縮小／強制買戻しの推定ほか）。
    // 日報＝明細・月報＝回数（04_report-templates・ADR-0016 決定15）。
    private static void AppendRiskControlRecords(StringBuilder sb, ReportView view)
    {
        AppendMarginReductions(sb, view);
        AppendFxSourceStatus(sb, view);
        AppendBuyInInferences(sb, view);
        AppendShortSelling(sb, view);
        AppendControlActivations(sb, view);
        AppendLlmModelUsage(sb, view);
    }

    // FR-16, #563, IADR-0269, 04_report-templates 日報 §2: 取引履歴（全明細）＋取引詳細＋見送り判断。
    //
    // 🔴 **これが #563 で欠けていた結線そのものである。** 呼び出しを外すと日報のゴールデンが全文で赤くなる
    //（レンダラ単体テストだけでは再発を捕まえられない——「呼ばれたこと」と「結果が出口へ出たこと」は別の事実）。
    //
    // 🔴 **`null`（明細を組み立てられていない）を「約定なし」へ潰さない。** 約定 0 件は空の Lines で表す。
    private static void AppendTradeHistory(StringBuilder sb, ReportView view)
    {
        if (view.TradeHistory is not { } history)
        {
            sb.Append("## 2. 取引履歴（全明細）\n\n");
            sb.Append("- **取引履歴を照会できませんでした（供給元がありません）**: "
                + "「当日の約定なし」とは区別しています。**0 件ではありません。**\n\n");
            return;
        }

        sb.Append(TradeHistoryRenderer.RenderMarkdown(history));
        sb.Append('\n');
    }

    // FR-06, FR-16, #563, IADR-0269, 04_report-templates 日報 §3: ポジション一覧（当日終了時点）。
    //
    // 🔴 **`null`（照会できていない）を空列（建玉なし）へ潰さない。**
    // 「建玉ゼロ」は重い事実であり、照会不能と同じに書けば「今は何も持っていない」と読める。
    private static void AppendPositions(StringBuilder sb, ReportView view)
    {
        sb.Append("## 3. ポジション一覧（当日終了時点）\n\n");

        // 各節は自前で "\n" を前置するため、本節は**末尾に空行を残さない**（残すと空行が 2 つ並ぶ）。
        if (view.Positions is not { } positions)
        {
            sb.Append("- **建玉を照会できませんでした（供給元がありません）**: "
                + "「建玉なし」とは区別しています。**0 件ではありません。**\n");
            return;
        }

        if (positions.Count == 0)
        {
            sb.Append("（当日終了時点の建玉なし）\n");
            return;
        }

        sb.Append("| 市場 | 銘柄 | 方向 | 数量 | 平均取得単価 | 現在値 | 評価損益 | 損切りライン | 借株料累計 | 保有日数 |\n");
        sb.Append("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");
        foreach (var p in positions)
        {
            // 借株料はショートのみ記載し、ロングは「—」とする（04_report-templates 日報 §3 の注記）。
            var borrowFee = p.Side == TradeSide.Buy ? "—" : DecimalOrUnsupplied(p.BorrowFeeTotal);
            sb.Append(CultureInfo.InvariantCulture,
                $"| {MarketLabel(p.Market)} | {p.Symbol} | {DirectionLabel(p.Side)} | {Quantity(p.Quantity)} | "
                    + $"{Price(p.AverageEntryPrice)} | {DecimalOrUnsupplied(p.CurrentPrice)} | "
                    + $"{SignedOrUnsupplied(p.UnrealizedPnl)} | {Price(p.StopLossPrice)} | {borrowFee} | "
                    + $"{IntOrUnsupplied(p.HoldingDays)} |\n");
        }

        sb.Append('\n');
        sb.Append("- 価格・評価損益は**市場の現地通貨**建てです（供給元の単位）。"
            + "**損切りラインは、取引判断が決めた実値が無い建玉では既定比率からの近似値**です。\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"- `{UnsuppliedCell}` は**記録源が無い**ことを表します。**「該当なし」「0」とは区別しています。**\n");
        sb.Append("  - **現在値・評価損益**: 市場データ源から現在値を引けなかった銘柄（**評価損益 0 ではありません**）。\n");
        sb.Append("  - **借株料累計**: 建玉開始からの累計の記録源がありません"
            + "（当期間の計上額は §4「空売りの記録」にあり、累計とは別物です）。\n");
        sb.Append("  - **保有日数**: 台帳の射影が建玉の開始時刻を持っていません。\n");
    }

    /// <summary>表のセルで「記録源が無い」ことを表す標識（TradeHistoryRenderer と同一）。</summary>
    private const string UnsuppliedCell = "**未供給**";

    private static string MarketLabel(Market market) => market switch
    {
        Market.Japan => "JP",
        Market.UnitedStates => "US",
        _ => market.ToString(),
    };

    // 04_report-templates 日報 §3: 「方向」は **ロング（現物・信用買い） / ショート（空売り）** で記す。
    private static string DirectionLabel(TradeSide side) => side == TradeSide.Buy ? "ロング" : "ショート";

    private static string Quantity(int quantity) => quantity.ToString("#,##0", CultureInfo.InvariantCulture);

    private static string Price(decimal value) => value.ToString("#,##0.##", CultureInfo.InvariantCulture);

    private static string DecimalOrUnsupplied(decimal? value) => value is { } v ? Price(v) : UnsuppliedCell;

    private static string SignedOrUnsupplied(decimal? value) =>
        value is { } v ? v.ToString("+#,##0.##;-#,##0.##;0", CultureInfo.InvariantCulture) : UnsuppliedCell;

    private static string IntOrUnsupplied(int? value) =>
        value is { } v ? v.ToString(CultureInfo.InvariantCulture) : UnsuppliedCell;

    // FR-06, FR-07, ADR-0017 決定4-(1), #335, IADR-0217: 報告書のメタ情報として「実際に使用したモデル」を残す。
    //
    // ADR-0017 決定4 の明文: 「**月報が第 1 候補で書かれたのか第 2 候補で書かれたのかは、その月報を
    // 次の 1 か月の方針書として採用する際の判断材料である。**」フォールバック機構の最大の危険は、
    // 設定ミスや制度変更が「動いているように見える」ことで発見されなくなる点にある（＝沈黙のフォールバック）。
    //
    // **未供給（null）は節ごと出さない。** 「照会できていない」を「第 1 候補で書かれた」と読ませないための
    // 扱いであり、AppendFxSourceStatus と同じ規律である（既存の描画結果も変わらない）。
    private static void AppendLlmModelUsage(StringBuilder sb, ReportView view)
    {
        if (view.LlmModelUsage is not { } usage)
            return;

        sb.Append("\n### 散文生成に使用した LLM\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"- 用途: {usage.Purpose}\n");
        sb.Append(CultureInfo.InvariantCulture, $"- 割当（第 1 候補）: {usage.ExpectedModel ?? "（割当なし）"}\n");
        sb.Append(CultureInfo.InvariantCulture, $"- 実際に使用したモデル: {usage.EffectiveModel ?? "（不明）"}\n");
        if (usage.IsPrimary)
        {
            sb.Append("- フォールバック: 発火なし（第 1 候補で生成）\n");
            return;
        }

        sb.Append(CultureInfo.InvariantCulture,
            $"- **フォールバック: 発火あり（{usage.Outcome}）**: 第 1 候補以外のモデルで生成されています。品質が第 1 候補と同一である保証はありません。\n");
    }

    // FR-10, FR-06, UC-06, ADR-0016 決定4（2026-08-06 改訂）・決定15（2026-08-06 追記）, #419, IADR-0159 決定3:
    // 「強制買戻し（推定）」の記録。日報は**当日の発生有無**、月報は**当月の発生回数**（決定15）。
    //
    // **必ず「推定」と明示する。** 決定4 の改訂はイベント検知の供給元が無いことを受けて事後の突合による推定へ
    // 切り替え、「**推定であることを運用者へ示す**（日報・通知の文言で『強制買戻しと推定』と明示し、確定事実として
    // 扱わない）」と定めた。決定15 はこの扱いが**月報の発生回数にも同じく及ぶ**と明記している。
    //
    // **供給が無い間は 0 件と書かない**（決定15）——「強制買戻しは起きていない」と読めるためである。
    // 空列（推定 0 件）と null（供給なし）を区別する。
    private static void AppendBuyInInferences(StringBuilder sb, ReportView view)
    {
        if (view.Kind == ReportKind.Weekly)
            return; // 計画が週報への記載を求めていない（求められていない節を勝手に増やさない）。

        sb.Append("\n### 強制買戻し（推定）");
        sb.Append(view.Kind == ReportKind.Monthly ? "（当月）\n\n" : "（当日）\n\n");

        if (view.BuyInInferences is not { } inferences)
        {
            // 決定15: 供給が無いことを 0 件・「なし」と書かない。
            sb.Append("- **記録を照会できませんでした（供給元がありません）**: "
                + "「発生なし」とは区別しています。**0 件ではありません。**\n");
            return;
        }

        if (view.Kind == ReportKind.Monthly)
        {
            // 月報は回数のみ。**集計元は推定の件数**であり、`BuyInBanned`（拒否理由）の件数ではない（決定15）。
            sb.Append(CultureInfo.InvariantCulture,
                $"- **強制買戻し（推定）{inferences.Count} 件**（**推定**であり確定した事実ではありません。"
                    + $"個々の内容は該当日報を参照）\n");
            return;
        }

        if (inferences.Count == 0)
        {
            sb.Append("- **発生の有無（推定）**: なし（当日の推定は 0 件）\n");
            return;
        }

        sb.Append(CultureInfo.InvariantCulture,
            $"- **発生の有無（推定）**: **あり — 強制買戻しと推定 {inferences.Count} 件**"
                + $"（イベントとしての検知ではなく、建玉の消失を自らの決済指示と突合した**推定**です。"
                + $"確定した事実として扱わないでください）\n\n");
        sb.Append("| # | 銘柄 | 台帳の空売り | ブローカの空売り | 処理中の決済 | 説明できない消失 | 空売り禁止（〜） | 推定日時 |\n");
        sb.Append("| --- | --- | --- | --- | --- | --- | --- | --- |\n");

        var index = 0;
        foreach (var e in inferences.OrderBy(e => e.InferredAt))
        {
            index++;
            sb.Append(CultureInfo.InvariantCulture,
                $"| {index} | {e.Symbol}/{e.Market} | {e.LedgerShortQuantity} 株 | {e.BrokerShortQuantity} 株 | "
                + $"{e.InFlightCloseQuantity} 株 | {e.NewlyInferredQuantity} 株 | {e.BanUntil:yyyy-MM-dd} | "
                + $"{e.InferredAt:yyyy-MM-dd HH:mm}Z |\n");
        }
    }

    // FR-06, FR-10, FR-17, UC-06, #381, ADR-0022 決定1・決定2・決定5, IADR-0196: 為替の情報源の状態。
    //
    // 🔴 **計画が「日報へ記録する」と明示的に求めている**——フォールバックへ切り替わった事実と
    // **切り替わっていた期間**（決定2）、鮮度警告（決定5）、そして**出典のクレジット表記**（決定1）。
    //
    // **「劣化しなかった」ことも書く。** 節ごと消すと、読み手は
    // 「劣化が無かったのか／節を出し忘れたのか」を区別できない。
    private static void AppendFxSourceStatus(StringBuilder sb, ReportView view)
    {
        if (view.Kind == ReportKind.Weekly)
            return;

        sb.Append("\n### 為替レートの情報源\n\n");

        // 🔴 照会不能を「切替なし」と書かない（IADR-0196 決定3）。劣化を隠したのと同じ結果になる。
        if (view.FxSourceStatus is not { } fx)
        {
            sb.Append("- **状態を照会できませんでした（要確認）**: 「切替なし」とは区別しています。\n");
            return;
        }

        // 🔴 **クレジット表記は劣化の有無と無関係に出す。** ADR-0022 決定1 が求めるのは
        // 「使ったなら出典を書く」ことであり、劣化しなかった期間こそ普通に日銀を使っている。
        // 🔴 #381 供給結線 / IADR-0199 決定5: **証拠が支える範囲までしか書かない。**
        // 旧文は「**第一の情報源から取得できており**、鮮度警告もありません」だったが、
        // **記録は遷移時にしか残らない**（IADR-0196 決定1）ため、**記録が無いことは
        // 「第一の源を使った」ことを意味しない**——為替を一度も使わなかった期間と区別が付かない。
        // IADR-0196 決定3（照会不能を「切替なし」と書かない）と同じ規律を 1 段深く当てたものである。
        // 🔴 #513 / IADR-0225 決定E: **証拠ができたので、外していた文言を戻す。**
        // 使用記録（暦日ごと・rank ≤ 1）があるなら「第一の情報源から取得できていた」は
        // **台帳が支える事実**である。**証拠が無ければ従来どおり書かない**（IADR-0199 決定5 は不変）。
        if (fx.IsClean)
        {
            var primaries = fx.PrimarySourceNames;
            sb.Append(primaries.Count > 0
                ? $"- 期間内は**第一の情報源（{string.Join("・", primaries)}）から取得できており**、"
                    + "情報源の切替・鮮度警告・鮮度切れでの決済の記録はありません。\n"
                : "- 期間内に、情報源の切替・鮮度警告・鮮度切れでの決済の記録はありません。\n");
            AppendFxCredits(sb, fx);
            return;
        }

        // 月報は**回数のみ**。個々の内容は該当日報に委ねる——直前後の節（維持率割れ・強制買戻し）と
        // 同じ規律である（「日報＝明細・月報＝回数」）。鮮度警告は暦日ごとに 1 件出るため、
        // **月報で明細にすると 20 行を超えて他の節を押し流す**（AI レビューの指摘・2026-08-15）。
        if (view.Kind == ReportKind.Monthly)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"- **フォールバックへの切替 {fx.FellBacks.Count} 件 / 復帰 {fx.Restorations.Count} 件 / "
                + $"鮮度警告 {fx.StaleWarnings.Count} 件"
                + $"（うち新規建て停止 {fx.StaleWarnings.Count(s => s.EntryBlocked)} 件）/ "
                + $"鮮度切れでの決済 {fx.StaleCloses.Count} 件**（個々の内容は該当日報を参照）\n");
            AppendFxCredits(sb, fx);
            return;
        }

        foreach (var e in fx.FellBacks)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"- **フォールバックへ切替**: {e.Quote} を {e.SourceName}（優先度 {e.Rank}/{e.TotalSources}）から取得"
                + $"（{e.OccurredAt:yyyy-MM-dd HH:mm}Z〜）。鮮度が週次へ悪化し得ます。**新規建ては止まっていません**。{'\n'}");
        }

        // 期間はイベント自身が持つ（受け手が引き算しない・IADR-0196 決定1）。
        foreach (var e in fx.Restorations)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"- **第一の情報源へ復帰**: {e.Quote}／{e.SourceName}（{e.OccurredAt:yyyy-MM-dd HH:mm}Z）。"
                + $"フォールバックしていた期間 {FormatDuration(e.FallbackDuration)}。{'\n'}");
        }

        // 🔴 #381 停止側 / IADR-0198: **警告と停止を同じ文で書かない。**
        // 停止域（EntryBlocked）でも「新規建ては止まっていません」と書いていたため、
        // **統制が発動した日の日報が、発動していないと読める**状態だった。
        foreach (var e in fx.StaleWarnings)
        {
            var label = e.EntryBlocked ? "鮮度切れ（新規建て停止）" : "鮮度警告";
            var effect = e.EntryBlocked
                ? "**新規建てを停止しました。手仕舞い・損切りは止めていません**"
                : "**直近レートで続行しており新規建ては止まっていません**";

            sb.Append(CultureInfo.InvariantCulture,
                $"- **{label}**: {e.Quote} 観測日 {e.AsOf:yyyy-MM-dd}・経過 {e.AgeDays:0.#} 日"
                + $"（警告 {e.WarnThresholdDays:0.#} 日 / 停止 {e.MaxAgeDays:0.#} 日）。{effect}。{'\n'}");
        }

        // 🔴 **取引そのものの記録**（IADR-0198 決定3）。状態の行と別に、**1 件ずつ**出す——
        // 鮮度警告は 1 日 1 回へ抑止されるが、**決済は件数も金額も後から復元できなければならない。**
        foreach (var e in fx.StaleCloses)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"- **鮮度切れのレートで決済**: {e.Symbol}/{e.Market} 数量 {e.Quantity}・"
                + $"換算率 {e.FxRateToBase}（{e.Quote}→基準通貨・観測日 {e.RateAsOf:yyyy-MM-dd}・"
                + $"{e.AgeDays:0.#} 日前）。**計画どおり手仕舞いは止めていません**が、"
                + $"**換算額は実勢から乖離し得ます**。{'\n'}");
        }

        AppendFxCredits(sb, fx);
    }

    // ADR-0022 決定1: 出典の明記。**実際に使った情報源のぶんだけ**出す
    // （使っていない情報源のクレジットを載せるのは事実に反する・IADR-0196 決定4）。
    //
    // **3 つの出口すべてから呼ぶ**（劣化なし／月報の回数／日報の明細）。
    // 出口ごとに書くと、**どれか 1 つで書き忘れても他が通るため気づかない**。
    private static void AppendFxCredits(StringBuilder sb, FxSourceStatus fx)
    {
        foreach (var credit in fx.PrimarySourceCredits)
        {
            // 文字列のみのため書式指定は不要（culture 依存の値を含まない）。
            sb.Append("- 出典: ").Append(credit).Append('\n');
        }

        if (fx.PrimarySourceCredits.Count > 0)
        {
            return;
        }

        // 🔴 #513 / IADR-0225 決定E: **「クレジットが空」と「使った源が分からない」は別である。**
        // 使用記録が入ったことで、**使った源は分かるがその源はクレジット表記を求めていない**
        // （FRED だけを使った期間）という状態が生じた。ここを「特定できません」と書くと**端的に誤りになる**。
        var used = fx.UsedSourceNames;
        if (used.Count > 0)
        {
            sb.Append("- 出典: ").Append(string.Join("・", used))
                .Append("（クレジット表記を求めていない情報源です）\n");
            return;
        }

        // 🔴 #381 供給結線 / IADR-0199 決定5: **出典が空であることを黙って通さない。**
        // ここを無言で済ませると、読み手は「出典の記載を忘れている」のか
        // 「**書ける根拠が無い**」のかを区別できない——本節が一貫して避けてきた形である。
        sb.Append("- 出典: **記録からは特定できません**"
            + "（期間内に情報源の使用記録が残っていないため、使用した情報源を判別できません）。\n");
    }

    // 期間は意味のある単位まで（秒まで書くと読み手が桁を数えることになる）。監査・通知と同じ規則。
    private static string FormatDuration(TimeSpan d) =>
        d.TotalDays >= 1 ? d.TotalDays.ToString("0.#", CultureInfo.InvariantCulture) + " 日"
        : d.TotalHours >= 1 ? d.TotalHours.ToString("0.#", CultureInfo.InvariantCulture) + " 時間"
        : d.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture) + " 分";

    // FR-10, FR-06, UC-06, #330, IADR-0133 決定7, 04_report-templates（日報 §4・月報 §6）:
    // 「維持率割れによる自動縮小」の記録。**システムが自ら決済注文を発注する唯一の統制**であるため、
    // 記録が無ければ「知らないうちに建玉が減っていた」状態になる。
    //
    // 日報は**発動の有無・決済した建玉・決済前後の維持率**（表 7 列）、月報は**当月の発動回数**。
    // 週報は計画が記載を求めていないため出さない（求められていない節を勝手に増やさない）。
    //
    // 発動が無い日は「なし」、無い月は「0 件」と**明記する**（計画: 空欄と「なし」を区別する）。
    // **照会できなかった場合は「なし」と書かない**——発動を隠したのと同じ結果になるため区別する。
    private static void AppendMarginReductions(StringBuilder sb, ReportView view)
    {
        if (view.Kind == ReportKind.Weekly)
            return;

        sb.Append("\n## 4. リスク統制の記録\n\n");
        sb.Append("### 維持率割れによる自動縮小");
        sb.Append(view.Kind == ReportKind.Monthly ? "（当月）\n\n" : "（当日）\n\n");

        if (view.MarginReductions is not { } reductions)
        {
            sb.Append("- **記録を照会できませんでした（要確認）**: 「発動なし」とは区別しています。\n");
            return;
        }

        if (view.Kind == ReportKind.Monthly)
        {
            // 月報は回数のみ。個々の内容は該当日報に委ねる（04_report-templates 月報 §6 の裁定）。
            sb.Append(CultureInfo.InvariantCulture,
                $"- **発動回数: {reductions.Count} 件**（個々の発動内容〔決済した建玉・決済前後の維持率〕は該当日報を参照）\n");
            return;
        }

        if (reductions.Count == 0)
        {
            sb.Append("- **発動の有無**: なし\n");
            return;
        }

        sb.Append(CultureInfo.InvariantCulture, $"- **発動の有無**: あり — {reductions.Count} 回\n\n");
        sb.Append("| # | 時刻 | 決済前の維持率 | 閾値 | 回復目標（閾値+5pt） | 決済した建玉（銘柄・方向・数量・必要証拠金） | 決済後の維持率 |\n");
        sb.Append("| --- | --- | --- | --- | --- | --- | --- |\n");

        var index = 0;
        foreach (var r in reductions.OrderBy(r => r.ExecutedAt))
        {
            index++;
            var legs = string.Join("<br>", r.Items.Select(i =>
                $"{i.Symbol} / {(i.PositionSide == TradeSide.Buy ? "ロング" : "ショート")} / {i.Quantity} 株 / "
                + $"{i.RequiredMarginUsd.ToString("N2", CultureInfo.InvariantCulture)} USD"));

            sb.Append(CultureInfo.InvariantCulture,
                $"| {index} | {r.ExecutedAt:HH:mm} | {Percent(r.RatioBefore)} | {Percent(r.Threshold)} | "
                + $"{Percent(r.RecoveryTarget)} | {legs} | {(r.RatioAfter is { } after ? Percent(after) : "建玉なし")} |\n");
        }
    }

    // FR-06, #338, ADR-0016 決定15, ADR-0027 決定1・決定4, 04_report-templates 日報 §4 / 月報 §6.1:
    // 空売りの記録（借株コスト）。
    //
    // 🔴 **未計上（料率が取れなかった日）を 0 円として合計へ混ぜない**（ADR-0027 決定4）。
    // 合計と別に件数を出すことで、「借株コストが安かった」と「費用を計上できていなかった」を読み分けられるようにする。
    //
    // 空売り建玉が無い期間も **「0 件」と明記する**（計画: 空欄と 0 を区別する）。
    // **照会できなかった場合は「0」と書かない**——費用が無かったのと同じに読めるため区別する。
    private static void AppendShortSelling(StringBuilder sb, ReportView view)
    {
        if (view.Kind == ReportKind.Weekly)
            return; // 計画は週報への記載を求めていない（求められていない節を勝手に増やさない）。

        sb.Append("\n### 空売りの記録");
        sb.Append(view.Kind == ReportKind.Monthly ? "（当月）\n\n" : "（当日）\n\n");

        if (view.BorrowFees is not { } record)
        {
            sb.Append("- **借株コストを照会できませんでした（供給元がありません）**: "
                + "「0 USD」とは区別しています。**費用が無かったのではありません。**\n");
            return;
        }

        var summary = BorrowFeeAggregator.Aggregate(record);

        if (record.Accruals.Count == 0 && record.Unavailable.Count == 0)
        {
            sb.Append("- **空売り建玉: 0 件**（借株料の計上・未計上のいずれも記録がありません）\n");
            return;
        }

        sb.Append(CultureInfo.InvariantCulture,
            $"- **借株コスト（経費区分 BorrowFee）合計: {ReportAmountFormat.Base(summary.TotalUsd)}**"
            + $"（計上 {record.Accruals.Count} 件）\n");

        sb.Append(summary.MaxRateAnnual is { } max
            ? $"- 適用年率の最大: {Percent(max)}（上限 {Percent(BorrowFeeAggregator.MaxAnnualRate)}）\n"
            : "- 適用年率: **計上が無いため該当なし**（0% ではありません）\n");

        // 🔴 未計上の件数は必ず出す。**0 件のときも出す**——「未計上があったのか無かったのか」を
        // 読み手が節の有無で推測しなければならない状態にしない。
        sb.Append(record.Unavailable.Count == 0
            ? "- 料率を取得できず未計上だった日: **なし**\n"
            : $"- **料率を取得できず未計上だった日: {record.Unavailable.Count} 件**"
                + "（0 円として合計へ含めていません。**実際の借株コストは上の合計より大きくなります**）\n");

        if (summary.BySymbolUsd.Count == 0)
            return;

        sb.Append("\n| 銘柄 | 借株コスト | 適用年率（最大） |\n");
        sb.Append("| --- | --- | --- |\n");
        foreach (var (symbol, amount, rate) in summary.BySymbolUsd)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"| {symbol} | {ReportAmountFormat.Base(amount)} | {Percent(rate)} |\n");
        }
    }

    // FR-06, FR-10, FR-20, #338, IADR-0253, 04_report-templates 月報 §6 / 04_workflows/03 月報 3:
    // **統制作動状況**。作動機会があり作動しなかった統制と、作動機会そのものが存在しなかった統制を**分けて**出す。
    //
    // 🔴 計画の明文: 「どちらも『0 件』と報告すると**検証されたものと検証されなかったものの区別が失われる**。
    // Stage 2 昇格の判断には後者の一覧が要る。」
    //
    // 🔴 **1 つの一覧に混ぜない。** 見出しを分け、片方が空でも**見出しごと消さない**——
    // 節が無いことは「該当が無かった」とも「出し忘れた」とも読めるためである。
    private static void AppendControlActivations(StringBuilder sb, ReportView view)
    {
        if (view.Kind != ReportKind.Monthly)
            return; // 計画は月報にのみ本節を求めている。

        // 🔴 **新しい供給を要求しない。** 判定の入力は本ビューが既に持っている証拠だけである
        // （維持率割れ自動縮小・強制買戻し推定・借株料・為替情報源の状態）。
        var report = ControlActivationCatalog.Evaluate(
            view.MarginReductions, view.BuyInInferences, view.BorrowFees, view.FxSourceStatus);

        sb.Append("\n### 当月の統制作動状況\n\n");
        sb.Append("> **「作動機会が無かった統制」と「統制違反 0 件」は別の事実である。**"
            + " 前者は未検証であり、Stage 2 昇格の判断には後者と分けて読む必要がある。\n\n");

        AppendControlGroup(sb, "1. 作動機会があり、作動しなかった統制（この統制については違反 0 件を主張できる）",
            report.OpportunityWithoutActivation,
            "該当なし（作動機会があり作動しなかった統制はありません）");

        AppendControlGroup(sb, "2. **作動機会そのものが存在しなかった統制（未検証）**",
            report.NoOpportunity,
            "該当なし（当月はすべての統制に作動機会がありました）");

        AppendControlGroup(sb, "3. 当月に作動した統制",
            report.Activated,
            "該当なし（当月に作動した統制はありません）");

        AppendControlGroup(sb, "4. **判定に要る記録を照会できず、判定できなかった統制**",
            report.NotSupplied,
            "該当なし（すべての統制について記録を照会できました）");
    }

    // 統制の 1 グループ。**空でも見出しを出す**（節の有無で意味を持たせない）。
    private static void AppendControlGroup(
        StringBuilder sb, string heading, IReadOnlyList<ControlActivation> controls, string emptyNote)
    {
        sb.Append(CultureInfo.InvariantCulture, $"{heading}\n\n");

        if (controls.Count == 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"- {emptyNote}\n\n");
            return;
        }

        foreach (var c in controls)
            sb.Append(CultureInfo.InvariantCulture, $"- {c.Name}: {c.Evidence}\n");

        sb.Append('\n');
    }

    // FR-06, FR-20, #338, INDEX 決定34, 04_report-templates 日報 §1 / 月報 §6.2: OpenD 稼働率。
    // 日報は**当日の稼働率と Stage 1 日数への算入可否**（サマリ行）、月報は**分布**（本節）。
    //
    // 🔴 **未供給を 0% と書かない。** 稼働率 0% は「終日停止していた」という重い事実であり、
    // 「観測を照会できていない」とは別物である。
    private static void AppendUptime(StringBuilder sb, ReportView view)
    {
        if (view.Kind != ReportKind.Monthly)
            return; // 日報はサマリ行で出す（本節は月報の分布）。週報は計画が求めていない。

        sb.Append("\n## 5. 当月の OpenD 稼働率分布\n\n");

        if (view.Uptime is not { } uptime)
        {
            sb.Append("- **稼働率の観測を照会できませんでした（供給元がありません）**: "
                + "「稼働率 0%」「算入 0 日」とは区別しています。\n");
            return;
        }

        var d = OpenDUptimeAggregator.Distribution(uptime);

        sb.Append("| 稼働率の区分（その日の通常取引時間に対する比率） | 日数 |\n");
        sb.Append("| --- | --- |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| 100% | {d.FullDays} 日 |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| 50〜99%（Stage 1 の日数に算入する） | {d.PartialCountedDays} 日 |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| 50% 未満（Stage 1 の日数に算入しない） | {d.NotCountedDays} 日 |\n\n");

        sb.Append(uptime.Stage1CumulativeCountedDays is { } cumulative
            ? $"- Stage 1 の累計算入日数: {cumulative} / {OpenDUptimeAggregator.Stage1TargetDays} 日\n"
            : $"- Stage 1 の累計算入日数: **供給されていません**（当月の算入は {d.CountedDays} 日"
                + $" / 目標 {OpenDUptimeAggregator.Stage1TargetDays} 日。**累計ではありません**）\n");

        sb.Append("- **稼働率 50% 台の日が常態化していないか**を確認する"
            + "（閾値方式のため、稼働率 51% の日も 100% の日も同じ 1 日として数えられる）。\n");
    }

    // FR-06, FR-15, FR-20, #338, 04_report-templates 月報 §5: バックテスト / SIMULATE / 実弾の三者比較。
    //
    // 🔴 **「空欄」と「値が 0」を区別できる表記にする**（計画の明文）。
    // 空欄は「その段をまだ走らせていない」、0 は「走らせた結果 0 だった」であり、乖離の読み方が正反対になる。
    // 🔴 **合否判定には使わない**（人間が読む材料）。本節はどの判定にも入力されない。
    private static void AppendThreeWayComparison(StringBuilder sb, ReportView view)
    {
        if (view.Kind != ReportKind.Monthly)
            return;

        sb.Append("\n## 6. バックテスト / SIMULATE / 実弾の三者比較\n\n");

        if (view.ThreeWayComparison is not { } c)
        {
            sb.Append("- **三者比較の実績を照会できませんでした（供給元がありません）**: "
                + "「取引件数 0 件」とは区別しています。**走らせた結果 0 だったのではありません。**\n");
            return;
        }

        sb.Append("| 比較指標 | バックテスト | SIMULATE（Stage 1） | 実弾（Stage 2 以降） |\n");
        sb.Append("| --- | --- | --- | --- |\n");
        AppendMetricRow(sb, "勝率", c.WinRate, MetricFormat.Ratio);
        AppendMetricRow(sb, "平均損益", c.AveragePnlUsd, MetricFormat.BaseAmount);
        AppendMetricRow(sb, "最大ドローダウン", c.MaxDrawdown, MetricFormat.Ratio);
        AppendMetricRow(sb, "取引件数", c.TradeCount, MetricFormat.Count);
        sb.Append('\n');

        sb.Append(string.IsNullOrWhiteSpace(c.DivergenceNote)
            ? "- 差分が大きい指標の要因考察: **未記入**（① 証拠金条件の差 / ② 借株料の差 / ③ 執行の差 のいずれか）\n"
            : $"- 差分が大きい指標の要因考察: {c.DivergenceNote.Trim()}\n");
        sb.Append("- 「該当なし」はその段をまだ走らせていないことを表す。**値 0 とは区別する。**\n");

        // #569, IADR-0271: 🔴 **供給されていない指標を「該当なし」と読ませない。**
        // 表の「該当なし」は「その段をまだ走らせていない」を意味すると直前の行で宣言している。
        // 供給元が無いだけの指標を同じ記号で描くと、その宣言が嘘になる。行を分けて明記する。
        if (c.MaxDrawdown is { Backtest: null, Simulate: null, Live: null })
        {
            sb.Append("- 最大ドローダウンは**どの列も供給されていません**"
                + "（エクイティ曲線に対する比率であり、期間集計の権威源が無いため）。"
                + "**表中の「該当なし」とは別の理由です。**\n");
        }

        // #569, IADR-0271: 🔴 **発注先が記録されていない約定を黙って落とさない。**
        // 列へ算入しなかった件数を出さないと、読み手は「その段では 1 件も取引していない」と読む。
        if (c.UnattributedTradeCount > 0)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"- 発注先が記録されていない約定が {c.UnattributedTradeCount} 件あり、**どの列にも算入していません**（推定で寄せると、その列の実績が水増しされるため）。\n");
        }
    }

    private enum MetricFormat { Ratio, BaseAmount, Count }

    private static void AppendMetricRow(StringBuilder sb, string label, ThreeWayMetric metric, MetricFormat format)
    {
        sb.Append(CultureInfo.InvariantCulture,
            $"| {label} | {Cell(metric.Backtest, format)} | {Cell(metric.Simulate, format)} | {Cell(metric.Live, format)} |\n");
    }

    // 🔴 null は「該当なし」（空欄）であり 0 ではない。**"0" や "—" へ潰さない。**
    private static string Cell(decimal? value, MetricFormat format) => value switch
    {
        null => "該当なし",
        { } v when format == MetricFormat.Ratio => Percent(v),
        { } v when format == MetricFormat.BaseAmount => ReportAmountFormat.Base(v),
        { } v => v.ToString("0.###", CultureInfo.InvariantCulture) + " 件",
    };

    // FR-06, FR-16, #338, #282, #347, ADR-0017 決定2・決定4, INDEX 決定44, 04_report-templates 月報 §7:
    // **当月の LLM 利用実績**。
    //
    // 🔴 **取引判断の費用と報告書生成の費用は必ず分けて記載する**（計画の明文。
    // 「合算すると、どちらが上限に効いているか分からなくなる」）。
    // 🔴 **報告書生成は月次上限の対象外であって、計上しないという意味ではない**——#282 はまさに
    // 「対象外だから計測点が無い」状態であった。本節がその費用の**出口**である。
    // 🔴 **分割（材料は減らない）と切り詰め（材料が減る）を分けて数える**（決定44）。
    private static void AppendLlmUsage(StringBuilder sb, ReportView view)
    {
        if (view.Kind != ReportKind.Monthly)
            return;

        sb.Append("\n## 7. 当月の LLM 利用実績\n\n");

        if (view.LlmUsage is not { } record)
        {
            sb.Append("- **LLM の利用実績を照会できませんでした（供給元がありません）**: "
                + "「費用 0 円」「フォールバック 0 件」「スキップ 0 件」とは区別しています。\n");
            return;
        }

        var u = LlmUsageAggregator.Aggregate(record);

        sb.Append("| 項目 | 値 |\n");
        sb.Append("| --- | --- |\n");

        var ratio = LlmUsageAggregator.ConsumptionRatio(u.TradeDecisionCostJpy);
        sb.Append(CultureInfo.InvariantCulture,
            $"| 取引判断の費用実績（月次上限 {ReportAmountFormat.Threshold(LlmUsageAggregator.MonthlyLlmCostLimitJpy, Currency.Jpy)} に対する消費率） "
            + $"| {ReportAmountFormat.Jpy(u.TradeDecisionCostJpy)} / {(ratio is { } r ? Percent(r) : "算出不能")} |\n");

        sb.Append(CultureInfo.InvariantCulture,
            $"| 報告書生成の費用実績（上限の対象外） | {ReportCostBreakdown(u)} |\n");

        // 🔴 上限の対象でも報告書でもない用途（情報収集等）を**落とさない**。
        // 落とすと「どこにも現れない費用」ができ、#282 と同じ形が別の用途で再発する。
        sb.Append(CultureInfo.InvariantCulture,
            $"| その他の用途の費用実績（上限の対象外） | {ReportAmountFormat.Jpy(u.OtherCostJpy)} |\n");

        sb.Append(CultureInfo.InvariantCulture,
            $"| フォールバック発火回数（用途別・原因別） | {FallbackBreakdown(u)} |\n");

        sb.Append(CultureInfo.InvariantCulture,
            $"| モデル利用不能による取引判断スキップ回数 | {SkipBreakdown(u)} |\n");

        sb.Append(CultureInfo.InvariantCulture,
            $"| スクリーニング入力の分割回数・切り詰め発生件数 | {ScreeningBreakdown(record.ScreeningDegradation)} |\n");
    }

    private static string ReportCostBreakdown(LlmUsageSummary u) =>
        u.ReportCostJpyByPurpose.Count == 0
            ? $"{ReportAmountFormat.Jpy(0m)}（当月の報告書生成の費用計上はありません）"
            : string.Join(" / ", u.ReportCostJpyByPurpose.Select(e => $"{e.Purpose}: {ReportAmountFormat.Jpy(e.AmountJpy)}"));

    private static string FallbackBreakdown(LlmUsageSummary u) =>
        u.FallbacksByPurposeAndOutcome.Count == 0
            ? "0 件"
            : string.Join(" / ", u.FallbacksByPurposeAndOutcome.Select(e =>
                string.Format(CultureInfo.InvariantCulture, "{0}／{1}: {2} 件", e.Purpose, e.Outcome, e.Count)));

    private static string SkipBreakdown(LlmUsageSummary u) =>
        u.SkipCount == 0
            ? "0 件（**障害ではなく設計上の正常な結果である**。ADR-0017 決定2）"
            : string.Format(CultureInfo.InvariantCulture, "{0} 件（{1}）", u.SkipCount,
                string.Join(" / ", u.SkipsByReason.Select(e =>
                    string.Format(CultureInfo.InvariantCulture, "{0}: {1} 件", e.Reason, e.Count))));

    // 🔴 供給が無いことを **0 回 / 0 件と書かない**（決定44）。
    // 「静かに判断材料が減っていた」状態に気づけるようにするための記録であり、
    // 未供給を 0 と書けばその目的そのものが失われる。
    private static string ScreeningBreakdown(ScreeningDegradationCounts? counts)
    {
        if (counts is null)
            return "**供給されていません**（分割・切り詰めの計数はスクリーニング層が供給する。**0 回 / 0 件ではありません**）";

        var targets = counts.TruncatedTargets.Count == 0
            ? "内訳なし"
            : string.Join(" / ", counts.TruncatedTargets
                .OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => string.Format(CultureInfo.InvariantCulture, "{0}: {1}", e.Key, e.Value)));

        return string.Format(CultureInfo.InvariantCulture,
            "分割 {0} 回 / 切り詰め {1} 件（{2}）", counts.SplitCount, counts.TruncationCount, targets);
    }

    // 維持率の表記（小数第 1 位。04_report-templates の <n%> に合わせる）。
    // "P1" は文化により数値と % の間に空白が入るため使わない（テンプレートの表記は <n%>）。
    private static string Percent(decimal ratio) =>
        (ratio * 100m).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    // 種別ごとの見出し（漢字名・サマリ/散文/方針の各見出し）。
    private static (string Kanji, string Summary, string Narrative, string Policy) Labels(ReportKind kind) => kind switch
    {
        ReportKind.Weekly => ("週報", "## 1. 週間サマリ", "## 2. 振り返りと評価", "## 3. 翌週の方針"),
        ReportKind.Monthly => ("月報", "## 1. 月間サマリ", "## 2. 総括と評価", "## 3. 翌月の方針・投資方針"),
        // 🔴 #563, IADR-0269: 日報は §2・§3 を取引履歴・ポジション一覧へ譲り、散文と方針が §5・§6 へ下がる
        //（計画 04_report-templates の日報テンプレートの並び。実装は計画 §5 市況・特記事項と §6 振り返りを
        // 1 節に統合しているため、計画 §7 翌営業日の目標が実装では §6 になる）。週報・月報の番号は動かさない。
        _ => ("日報", "## 1. 当日サマリ", "## 5. 市況・振り返り", "## 6. 翌営業日の方針"),
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
                yield return ("週間実現損益（税引後・費用込み）", Amount(p.RealizedPnlNet));
                yield return ("勝率（勝ち取引/全決済取引）", WinRate(p));
                yield return ("取引回数（買/売/決済）", counts);
                yield return ("費用合計（手数料・諸費用・為替）", Amount(p.TotalCost));
                yield return ("週次目標に対する達成", Pending);
                break;

            case ReportKind.Monthly:
                yield return ("月間実現損益（税引後・費用込み）", Amount(p.RealizedPnlNet));
                // #338, 04_report-templates §数値の定義: **為替差損益は取引損益と混ぜず独立した行**で出す。
                yield return ("為替差損益（独立表示）", FxTranslationCell(view));
                yield return ("総資産（月初 → 月末）", Pending); // 04_report-templates の表記に一致（矢印前後に半角スペース）
                yield return ("年初来累計損益", Pending);
                yield return ("費用合計 / 費用率", $"{Amount(p.TotalCost)} / {Pending}");
                yield return ("月次目標に対する達成", Pending);
                break;

            default: // Daily
                yield return ("実現損益（税引後・費用込み）", Amount(p.RealizedPnlNet));
                // #338, 04_report-templates 日報 §1: 為替差損益は独立行。
                yield return ("為替差損益（独立表示）", FxTranslationCell(view));
                yield return ("評価損益（税引前・参考）", Amount(p.UnrealizedPnl));
                yield return ("取引回数（買/売/決済）", counts);
                yield return ("費用合計（手数料・諸費用・為替）", Amount(p.TotalCost));
                yield return ("源泉徴収税額", Amount(p.TaxWithheld));
                // INDEX 決定34: 当日の稼働率と Stage 1 日数への算入可否。
                yield return ("OpenD 稼働率（当日の通常取引時間に対する比率）", DailyUptimeCell(view));
                // ADR-0017 決定2: **障害ではなく設計上の正常な結果**。沈黙のスキップにしない。
                yield return ("モデル利用不能による取引判断スキップ", DailySkipCell(view));
                yield return ("日次目標に対する達成", Pending);
                break;
        }
    }

    // #338, 04_report-templates §数値の定義: 為替差損益（円換算により生じた損益）。
    //
    // 🔴 **未供給を 0 円と書かない。** 「為替では損得が無かった」と読めるためである。
    // 取引損益とは別の型（FxTranslationSummary）で持つため、この行が取引損益と合算されることはない。
    //
    // #611, IADR-0285 決定3・決定5: 供給時は**期末レートと観測日を併記**する（期末の再測定に使ったときだけ。
    // 「どの日の終値で再測定したか」を読み手が確かめられる）。認識時レートが未記録の USD 建て約定があって
    // 供給できないときは、**未記録の件数を明記**する（🔴 黙って落とさない。件数 0 の未供給は従来の文言のまま）。
    private static string FxTranslationCell(ReportView view)
    {
        if (view.FxTranslation is { } fx)
        {
            return fx is { PeriodEndRate: { } periodEndRate, PeriodEndRateAsOf: { } asOf }
                ? string.Format(CultureInfo.InvariantCulture,
                    "{0}（明細 {1} 件・期末レート {2:0.00} JPY/USD〔{3:yyyy-MM-dd} 観測〕）",
                    ReportAmountFormat.Jpy(fx.TranslationGainJpy), fx.EntryCount, periodEndRate, asOf)
                : string.Format(CultureInfo.InvariantCulture, "{0}（明細 {1} 件）",
                    ReportAmountFormat.Jpy(fx.TranslationGainJpy), fx.EntryCount);
        }

        return view.FxTranslationUnrecordedFillCount > 0
            ? string.Format(CultureInfo.InvariantCulture,
                "**供給されていません**（0 円ではありません。認識時レートが未記録の USD 建て約定 {0} 件）",
                view.FxTranslationUnrecordedFillCount)
            : "**供給されていません**（0 円ではありません）";
    }

    // INDEX 決定34, 06_daytrading-review §4.2: 当日の稼働率と Stage 1 日数への算入可否。
    // 🔴 **未供給を「稼働率 0%」と書かない**——終日停止という重い事実と混同させない。
    private static string DailyUptimeCell(ReportView view)
    {
        if (view.Uptime is not { } uptime || uptime.Days.Count == 0)
            return "**供給されていません**（稼働率 0% ではありません）";

        // 日報は 1 取引日ぶん。複数日が届いた場合は最新日を当日として扱う（決定的）。
        var day = uptime.Days.OrderBy(d => d.SessionDateEasternTime).Last();
        var counted = OpenDUptimeAggregator.IsCounted(day.UptimeRatio);
        return string.Format(CultureInfo.InvariantCulture,
            "{0} — Stage 1 の日数算入: {1}",
            Percent(day.UptimeRatio),
            counted ? "算入（50% 以上）" : "非算入（50% 未満）");
    }

    // ADR-0017 決定2: モデル利用不能による取引判断スキップ回数。
    // 🔴 **未供給を 0 件と書かない**（「取引機会を逸していない」と読める）。
    private static string DailySkipCell(ReportView view)
    {
        if (view.LlmUsage is not { } usage)
            return "**供給されていません**（0 件ではありません）";

        var u = LlmUsageAggregator.Aggregate(usage);
        return u.SkipCount == 0
            ? "0 件"
            : string.Format(CultureInfo.InvariantCulture, "{0} 件（{1}）", u.SkipCount,
                string.Join(" / ", u.SkipsByReason.Select(e =>
                    string.Format(CultureInfo.InvariantCulture, "{0}: {1} 件", e.Reason, e.Count))));
    }

    // 勝率（04_report-templates: 週報「<n%（n/n）>」形式）。決済ゼロなら "-（0/0）"。パーセントは文化非依存で整数表記する。
    private static string WinRate(PnlSummary p) =>
        p.RealizingTradeCount == 0
            ? "-（0/0）"
            : string.Format(CultureInfo.InvariantCulture, "{0:0}%（{1}/{2}）",
                (decimal)p.WinningTradeCount / p.RealizingTradeCount * 100m,
                p.WinningTradeCount, p.RealizingTradeCount);

    // 基準通貨建て表記（符号付き・千区切り・単位付き）。書式は ReportAmountFormat に単一化する
    // （IADR-0116: Discord 要約と同じ表記／#364, IADR-0152 決定6: 単位は MarketCurrency.Base から導く）。
    private static string Amount(decimal amount) => ReportAmountFormat.Base(amount);
}
