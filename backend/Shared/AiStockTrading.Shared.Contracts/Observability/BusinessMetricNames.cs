namespace AiStockTrading.Shared.Contracts.Observability;

/// <summary>
/// NFR-07, NFR-13, #287, IADR-0255: <b>業務メトリクスの名前レジストリ（単一情報源）。</b>
/// <para>
/// メトリクス名は<b>コードと Grafana ダッシュボードの間の契約</b>である。契約を 2 箇所に持つと
/// 片方が黙って古くなり、ダッシュボードのパネルが「値が来ない＝正常（0 件）」に見える最悪の形になる。
/// そのため名前は本クラスだけが持ち、(a) <see cref="BusinessMetrics"/> が作る計器名と、
/// (b) <c>deploy/observability/dashboards/*.json</c> が引く系列名の双方を、機械検査が本クラスへ突き合わせる。
/// </para>
/// <para>
/// <b>単位は計器の <c>unit</c> ではなく名前へ埋める</b>（<c>_ms</c> / <c>_jpy</c> / <c>_percent</c>）。
/// OTel の Prometheus 変換は <c>unit</c> を名前へ接尾するため（既存の
/// <c>http_server_duration_milliseconds_count</c> がその形）、<c>unit</c> を使うと
/// コード名 → Prometheus 名の変換規則が単位表に依存して増える。単位を名前へ埋めれば変換規則は
/// 「ドットを <c>_</c> へ置換」「Counter なら <c>_total</c>」「Histogram なら <c>_bucket</c>/<c>_count</c>/<c>_sum</c>」の
/// 3 つで閉じ、機械検査が 1 本の関数で書ける。
/// </para>
/// </summary>
public static class BusinessMetricNames
{
    /// <summary>業務メトリクスの Meter 名。OTel のメトリクスパイプラインへ <c>AddMeter</c> で登録する対象。</summary>
    public const string MeterName = "AiStockTrading.Business";

    /// <summary>FR-01, FR-02: 1 巡回で収集・正規化・KB 保存まで完了したアイテム数（取引サイクルの起点が動いているか）。</summary>
    public const string InformationItemsCollected = "ast.information.items_collected";

    /// <summary>FR-04: 取引判断の回数。タグ <c>action</c>（buy/sell/no-trade）・<c>trigger</c>（scheduled/price-movement）。</summary>
    public const string TradeCycleDecisions = "ast.trade_cycle.decisions";

    /// <summary>
    /// FR-04, FR-10, #891, IADR-0374: <b>見送り（発注意図を作らなかった判断）の理由の内訳。</b>
    /// タグ <c>reason</c>（<see cref="DecisionSkipReason"/> の各値）・<c>trigger</c>。
    /// <para>
    /// 🔴 <b><see cref="TradeCycleDecisions"/> の <c>action=no-trade</c> を置き換えない。</b>
    /// あちらは「判断が何回あり、そのうち何回が発注意図を作らなかったか」を数える系列であり、
    /// 既存ダッシュボードが引いている。理由を足すためにタグを増やすと**既存の集計が割れる**ため、
    /// <b>別カウンタとして並べる</b>。1 回の見送りで両方が 1 ずつ増えるので、
    /// 合計の突き合わせで計上漏れを検出できる。
    /// </para>
    /// <para>
    /// なぜ要るか: 理由が無いと「LLM が Hold を返した（正常・最も多い）」と
    /// 「保有照会が壊れていて新規建てだけが静かに止まっている」が<b>同じ 1 本の同じタグ値</b>に落ちる。
    /// 後者は手仕舞いが通るため「取引が全部止まった」形にはならず、**気付きにくい**。
    /// </para>
    /// </summary>
    public const string TradeCycleDecisionSkips = "ast.trade_cycle.decision_skips";

    /// <summary>FR-04: 取引判断 1 回の所要（ミリ秒）。タグ <c>trigger</c>。</summary>
    public const string TradeCycleDecisionDurationMs = "ast.trade_cycle.decision_duration_ms";

    /// <summary>
    /// NFR-01, #689, IADR-0307: <b>起点イベント → 発注完了</b>の端点間所要（ミリ秒）。タグ <c>trigger</c>。
    /// <para>
    /// NFR-01（価格変動検知から発注完了まで 5 分以内）は <c>trigger=price-movement</c> の系列で読む。
    /// 上の <see cref="TradeCycleDecisionDurationMs"/> は<b>判断 1 回</b>の所要であり、
    /// サービスを跨ぐ端点間ではない（#637 が「下地であって検証ではない」と自認していた区別）。
    /// </para>
    /// </summary>
    public const string TradeCycleOrderCompletionLatencyMs = "ast.trade_cycle.order_completion_latency_ms";

    /// <summary>
    /// NFR-02, #689, IADR-0307: <b>起点イベント → 記録完了</b>（監査台帳へ記録した時点）の端点間所要（ミリ秒）。
    /// タグ <c>trigger</c>。NFR-02（定時サイクル 1 回 10 分以内・収集→判断→発注→<b>記録</b>）は
    /// <c>trigger=scheduled</c> の系列で読む。
    /// </summary>
    public const string TradeCycleRecordCompletionLatencyMs = "ast.trade_cycle.record_completion_latency_ms";

    /// <summary>
    /// NFR-01, NFR-02, #689, IADR-0307: <b>端点間の所要を確定できなかった</b>件数。
    /// タグ <c>stage</c>（order-completion / record-completion）・<c>reason</c>。
    /// <para>
    /// 🔴 <b>「測れなかった」を 0 として記録しない。</b>起点が無い注文（owner 手仕舞い・自動縮小・
    /// 約定追跡の後追い）で 0 ms を計上すると、上の 2 本のヒストグラムが<b>目標を満たしているように見える</b>。
    /// 未観測は本カウンタへ理由つきで出し、ヒストグラムには 1 件も入れない。
    /// </para>
    /// </summary>
    public const string TradeCycleLatencyUnobserved = "ast.trade_cycle.latency_unobserved";

    /// <summary>
    /// FR-10, FR-19: 発注前審査の回数。タグ <c>outcome</c>（approved/rejected）。
    /// <b>承認も拒否も数える</b>——拒否だけを数えると「違反 0 件」と「そもそも審査が動いていない」を
    /// 区別できない（fail-open）。
    /// </summary>
    public const string RiskScreenings = "ast.risk.screenings";

    /// <summary>FR-10, FR-19: 拒否理由の内訳。タグ <c>reason</c>（<c>RejectionReason</c> の各値）。1 注文が複数理由を持てば各 1 件。</summary>
    public const string RiskRejections = "ast.risk.rejections";

    /// <summary>FR-05: 発注結果。タグ <c>status</c>（<c>OrderStatus</c>）・<c>provider</c>（<c>BrokerProvider</c>）。</summary>
    public const string OrderExecutions = "ast.order.executions";

    /// <summary>
    /// FR-05, FR-10: 発注そのものを見送った件数。タグ <c>reason</c>（<c>OrderDispatchForgoneReason</c>）。
    /// <b>ブローカーの拒否（<c>OrderStatus.Rejected</c>）とは別に数える</b>——見送りは注文が届いてすらいない状態であり、
    /// 混ぜると集計が接続障害で汚染される。
    /// </summary>
    public const string OrderDispatchForgone = "ast.order.dispatch_forgone";

    /// <summary>NFR-13: 計上した LLM 費用（円）。タグ <c>category</c>（Llm＝月次上限の対象 / LlmUncapped＝対象外）。</summary>
    public const string LlmCostJpy = "ast.llm.cost_jpy";

    /// <summary>NFR-13: 当月の LLM 費用が月次上限に占める割合（%）。80 で間隔延長・100 で停止。</summary>
    public const string LlmCostLimitRatioPercent = "ast.llm.cost_limit_ratio_percent";

    /// <summary>
    /// FR-01, ADR-0031（計画）決定2〜3, IADR-0292: プロセスごとの Finnhub 日次要求見積り（回/日）。
    /// 銘柄数の運用者申告（既定 0）が無ければ計上しない（挙動中立）。
    /// </summary>
    public const string FinnhubDailyVolumeEstimate = "ast.finnhub.daily_request_estimate";

    /// <summary>FR-01, ADR-0031（計画）決定3, IADR-0292: 上記見積りが暫定日次上限（既定300）に占める割合（%）。100 超で警告。</summary>
    public const string FinnhubDailyVolumeLimitRatioPercent = "ast.finnhub.daily_request_limit_ratio_percent";

    /// <summary>
    /// FR-10, #889, IADR-0372: <b>統制上限の基準資金（equity）を読んだ結果の内訳。</b>
    /// タグ <c>outcome</c>（<see cref="CapitalBaselineReadOutcome"/> の各値）。
    /// <para>
    /// 🔴 <b>「供給できた」の中を割るためにある。</b> 口座照会が残高 0 を返した日はその取引日の行が
    /// 1 行も書かれず、読み出しは<b>前取引日の正の値を鮮度が切れるまで返し続ける</b>。
    /// 値が返っている以上、統制は平常どおり動いて見え、<b>この状態は外から一切見えなかった</b>。
    /// </para>
    /// </summary>
    public const string RiskCapitalBaselineReads = "ast.risk.capital_baseline_reads";

    /// <summary>
    /// FR-03, FR-10, #957, IADR-0399: <b>市場監視が保有照会（<c>GET /risk-controls/open-positions</c>）の応答を
    /// そのまま評価できなかった行の件数。</b>タグ <c>reason</c>（identity-missing / stop-line-approximated /
    /// stop-line-unknown / response-unreadable）。
    /// <para>
    /// 🔴 <b>平常時の期待値は 0 件である。</b> 送り手は識別項目と損切りラインを常に載せる（送り手の応答型は非 nullable で、ラインの記録が無ければ
    /// 送り手自身が近似する）。出ているのは送り手と受け手の契約の食い違い（片方だけ先に配備した等）か送り手の値の異常の印であり、
    /// その行の損切り保護は欠けているか近似になっている。巡回そのものは止めない（他の行は評価を続ける）ため、
    /// ログを読みに行かない限り見えない —— だから数える。
    /// </para>
    /// </summary>
    public const string MarketMonitorPositionRowsDegraded = "ast.market_monitor.position_rows_degraded";

    /// <summary>タグ名: 判断の結果（buy / sell / no-trade）。</summary>
    public const string TagAction = "action";

    /// <summary>タグ名: 判断の起動契機（scheduled / price-movement）。</summary>
    public const string TagTrigger = "trigger";

    /// <summary>タグ名: 審査結果（approved / rejected）。</summary>
    public const string TagOutcome = "outcome";

    /// <summary>タグ名: 理由（拒否理由・見送り理由）。</summary>
    public const string TagReason = "reason";

    /// <summary>タグ名: 注文状態。</summary>
    public const string TagStatus = "status";

    /// <summary>タグ名: 発注先。</summary>
    public const string TagProvider = "provider";

    /// <summary>タグ名: 費用カテゴリ。</summary>
    public const string TagCategory = "category";

    /// <summary>NFR-01, NFR-02, #689: タグ名: 端点間計測の区間（order-completion / record-completion）。</summary>
    public const string TagStage = "stage";
}
