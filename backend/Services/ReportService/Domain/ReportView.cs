using AiStockTrading.Shared.Contracts.Events;

namespace ReportService.Domain;

// FR-06/16, 04_report-templates, IADR-0032: 報告書 Markdown 生成の入力（日報/週報/月報 共通）。数値は PnlSummary
// （コード集計値）、Narrative は LLM ドラフトの散文。ReportRenderer が Kind に応じて決定的に Markdown を組み立てる。
public sealed record ReportView
{
    /// <summary>報告書種別（Daily/Weekly/Monthly）。テンプレート・見出し・サマリ項目を切り替える。</summary>
    public required ReportKind Kind { get; init; }

    /// <summary>自然キー（"daily-2026-07-10" / "weekly-2026-W28" / "monthly-2026-07"）。</summary>
    public required string PeriodKey { get; init; }

    /// <summary>フロントマター period・タイトルの期間表記（ReportPeriod.Label）。</summary>
    public required string PeriodLabel { get; init; }

    /// <summary>対象市場（"JP"/"US" 等）。フロントマター markets。</summary>
    public IReadOnlyList<string> Markets { get; init; } = [];

    /// <summary>適用した全体前提条件バージョン（FR-17）。</summary>
    public int AssumptionsVersion { get; init; }

    /// <summary>参照した上位方針の PeriodKey（daily→週報 / weekly→月報 / monthly→前月報）。</summary>
    public string? BasedOn { get; init; }

    /// <summary>確定日時（未確定は null＝status: draft）。</summary>
    public DateTimeOffset? ConfirmedAt { get; init; }

    /// <summary>コード集計した損益サマリ（数値は LLM に計算させない・FR-16）。</summary>
    public required PnlSummary Pnl { get; init; }

    /// <summary>買い約定件数（取引回数の内訳）。</summary>
    public int BuyCount { get; init; }

    /// <summary>売り約定件数（取引回数の内訳）。</summary>
    public int SellCount { get; init; }

    /// <summary>翌期間の方針（確定で有効化される方針テキスト）。</summary>
    public string PolicySummary { get; init; } = string.Empty;

    /// <summary>LLM ドラフトの散文（市況・振り返り・評価等）。数値は含めない。</summary>
    public string Narrative { get; init; } = string.Empty;

    /// <summary>
    /// FR-10, UC-06, #330, IADR-0133 決定7: 当期間に発動した「維持率割れによる自動縮小」（04_report-templates
    /// 日報 §4・月報 §6）。**空列＝「発動なし」／`null`＝記録源に照会できていない**であり、両者を区別する。
    /// <para>
    /// 計画は「発動が無い日も『なし』と明記する（**空欄と「なし」を区別する**）」と定めた。同じ理由で、
    /// **照会できなかったものを「なし」と書いてはならない**——発動を隠したのと同じ結果になる。
    /// </para>
    /// </summary>
    public IReadOnlyList<MaintenanceMarginReductionExecuted>? MarginReductions { get; init; }

    /// <summary>
    /// FR-10, FR-06, UC-06, ADR-0016 決定4（2026-08-06 改訂）・決定15（2026-08-06 追記）, #419, IADR-0159 決定3:
    /// 当期間に**強制買戻しと推定した**件（日報＝発生有無・月報＝発生回数）。
    /// <para>
    /// **集計元は事後突合が推定した件であり、`RejectionReason.BuyInBanned`（拒否理由）の件数ではない**——
    /// 同理由は禁止期間中の発注拒否であり、1 回の強制買戻しに対して 30 日のあいだ何度でも発生し得る。
    /// 拒否件数を発生回数として報告すると実際より大きな数字が月報に載る（決定15 の明文）。
    /// **本ビューは拒否理由に関する入力を一切持たない**ことで、その取り違えを構造的に不可能にしている。
    /// </para>
    /// <para>
    /// **空列＝「推定 0 件」／`null`＝供給が無い**であり、両者を区別する。決定15 は
    /// 「**推定経路が入るまで発生回数は供給されない。供給が無い間は 0 件と表示してはならない**
    /// （『強制買戻しは起きていない』に見えるため）」と定めている。
    /// </para>
    /// </summary>
    public IReadOnlyList<BuyInInferred>? BuyInInferences { get; init; }

    /// <summary>
    /// FR-06, FR-10, FR-17, #381, ADR-0022 決定2・決定5, IADR-0196: 期間内の為替情報源の状態。
    /// <para>
    /// 🔴 <c>null</c> は「<b>照会できなかった</b>」であり「切替なし」ではない。
    /// 事象が無かった場合は<b>空の <see cref="FxSourceStatus"/></b> が入る。
    /// 両者を潰すと、劣化を隠したのと同じ結果になる（<see cref="MarginReductions"/> と同じ規律）。
    /// </para>
    /// </summary>
    public FxSourceStatus? FxSourceStatus { get; init; }

    /// <summary>
    /// FR-06, FR-07, ADR-0017 決定4-(1), #335, IADR-0217: この報告書の散文を<b>実際に生成したモデル</b>と、
    /// フォールバック発火の事実（可視化 3 経路の①）。
    /// <para>
    /// 🔴 <c>null</c> は「<b>モデルを照会できていない</b>」であり「フォールバックしていない」ではない
    /// （プレースホルダ生成・縮退時が該当する）。<see cref="FxSourceStatus"/> と同じ規律で、
    /// <b>節ごと出さない</b>ことで両者を潰さない。
    /// </para>
    /// </summary>
    public LlmModelUsage? LlmModelUsage { get; init; }

    /// <summary>
    /// FR-06, FR-16, #338, 04_report-templates 月報 §7 / 日報 §1, ADR-0017 決定2・決定4, INDEX 決定44:
    /// 当期間の LLM 利用実績（費用・フォールバック発火・取引判断スキップ・縮退件数）。
    /// <para>
    /// 🔴 <c>null</c> は「<b>照会できていない</b>」であり「利用実績が無い」ではない。
    /// 費用 0 円・スキップ 0 件と書けば、計上漏れ（#282 の形）が正常として読まれる。
    /// </para>
    /// </summary>
    public LlmUsageRecord? LlmUsage { get; init; }

    /// <summary>
    /// FR-06, #338, ADR-0016 決定15, ADR-0027, 04_report-templates 月報 §6.1 / 日報 §4: 当期間の借株料の記録。
    /// <para>🔴 <c>null</c> は「照会できていない」であり「借株コスト 0」ではない。</para>
    /// </summary>
    public BorrowFeeRecord? BorrowFees { get; init; }

    /// <summary>
    /// FR-06, FR-16, #338, 04_report-templates §数値の定義・日報 §1・月報 §1: <b>為替差損益</b>（独立表示）。
    /// <para>
    /// 🔴 <c>null</c> は「供給されていない」であり「為替差損益 0 円」ではない。
    /// 取引損益（<see cref="Pnl"/>）とは<b>別の型</b>で持つことで、合算する書き方を構造的に不可能にしている。
    /// </para>
    /// </summary>
    public FxTranslationSummary? FxTranslation { get; init; }

    /// <summary>
    /// FR-06, FR-16, #611, IADR-0286 決定3: 認識時レートが<b>未記録</b>だった USD 建て約定の件数。
    /// <para>
    /// 0 より大きいとき <see cref="FxTranslation"/> は必ず <c>null</c>（未供給）であり、描画は件数を明記する
    /// （🔴 <b>黙って落とさない</b>——未記録を落として残りだけ集計すると別の数値になるため部分集計は出さない。
    /// 既存行は推定で埋めない）。既定 0＝該当なし（従来の未供給描画と 1 バイトも変わらない）。
    /// </para>
    /// </summary>
    public int FxTranslationUnrecordedFillCount { get; init; }

    /// <summary>
    /// FR-06, FR-20, #338, INDEX 決定34, 04_report-templates 日報 §1 / 月報 §6.2: OpenD の稼働率。
    /// <para>🔴 <c>null</c> は「照会できていない」であり「稼働率 0%」ではない。</para>
    /// </summary>
    public OpenDUptimeRecord? Uptime { get; init; }

    /// <summary>
    /// FR-06, FR-15, FR-20, #338, 04_report-templates 月報 §5: バックテスト / SIMULATE / 実弾の三者比較。
    /// <para>
    /// 🔴 <c>null</c> は「照会できていない」。個々のセルの <c>null</c> は「<b>その段をまだ走らせていない</b>（空欄）」
    /// であり、<c>0</c>（値が 0）とは別である。
    /// </para>
    /// </summary>
    public ThreeWayComparison? ThreeWayComparison { get; init; }

    /// <summary>
    /// FR-16, #563, IADR-0269, 04_report-templates 日報 §2: 取引履歴（全明細）＋取引詳細＋見送り判断。
    /// <para>
    /// 🔴 <c>null</c> は「<b>明細を組み立てられていない</b>」であり「約定が無かった」ではない。
    /// 約定 0 件は<b>空の <see cref="TradeHistoryLine"/> 列を持つ <see cref="TradeHistoryView"/></b> で表し、
    /// レンダラが「（当日の約定なし）」を出す。両者を潰すと<b>節ごと消える</b>——
    /// それが #563（節が本文に一度も出ていなかった）の再発である。
    /// </para>
    /// <para>日報のみが持つ（週報・月報は粒度が違う集計であり、計画も明細を求めていない）。</para>
    /// </summary>
    public TradeHistoryView? TradeHistory { get; init; }

    /// <summary>
    /// FR-06, FR-07, FR-16, #615, IADR-0301, 04_report-templates 週報 §2/§3: <b>約定単位の損益帰属</b>
    /// （期間全体を 1 回だけ畳み込んだ結果）。週報の「日別推移」「ハイライト取引」はここから集計する。
    /// <para>
    /// 🔴 <c>null</c> は「<b>帰属を組み立てていない</b>」であり「約定が無かった」ではない。
    /// 約定 0 件は<b>空列</b>で表す（<see cref="TradeHistory"/> と同じ規律）。
    /// </para>
    /// <para>
    /// 🔴 <b>受け取った側で期間を切って畳み込み直さない。</b> 持ち越し建玉の平均取得単価がスライス内に
    /// 存在しないため、内訳の合計が <see cref="Pnl"/> と一致しなくなる（しかも全テストは緑のままである）。
    /// </para>
    /// </summary>
    public IReadOnlyList<FillPnlAttribution>? FillAttributions { get; init; }

    /// <summary>
    /// FR-06, FR-07, FR-16, FR-17, #615, IADR-0305, 04_report-templates 週報 §5: <b>費用の内訳と費用率</b>。
    /// <para>
    /// 🔴 <c>null</c> は「<b>内訳を組み立てていない</b>」であり「費用 0」ではない。
    /// <see cref="FillAttributions"/> と同じ規律で、<b>0 円と未供給を潰さない</b>。
    /// </para>
    /// <para>
    /// 🔴 <b>本型の <c>TotalCost</c> は <see cref="Pnl"/> の費用合計と一致する</b>——
    /// 同じ約定・同じ費用関数から数えているためである。<b>期間を切って集計し直した値を入れてはならない。</b>
    /// </para>
    /// </summary>
    public PeriodCostReview? CostReview { get; init; }

    /// <summary>
    /// FR-06, FR-16, #563, IADR-0269, 04_report-templates 日報 §3: ポジション一覧（当日終了時点）。
    /// <para>
    /// 🔴 <c>null</c> は「<b>照会できていない</b>」であり「建玉なし」ではない。
    /// 建玉が 1 件も無いことは<b>空列</b>で表す（<see cref="MarginReductions"/> と同じ規律）。
    /// </para>
    /// </summary>
    public IReadOnlyList<ReportPosition>? Positions { get; init; }
}
