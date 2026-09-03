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

    /// <summary>FR-04: 取引判断 1 回の所要（ミリ秒）。タグ <c>trigger</c>。</summary>
    public const string TradeCycleDecisionDurationMs = "ast.trade_cycle.decision_duration_ms";

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
}
