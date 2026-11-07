namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;

/// <summary>
/// NFR, IADR-0637: サービスに開く口の名前（<c>Auth:ServiceCallers:&lt;名前&gt;</c> の鍵）と、許す呼び手の既定の一覧（<see cref="DefaultCallers"/>）。
/// 母集合は作業仕様書 20261107_s2s-per-service-clients（呼び手の側の ExternalServices の呼び出しから引いた）。構成で与えると、その口の既定を置き換える。
/// 🔴 base appsettings.json に Auth の鍵を置けない（validate-runtime-scaffold）ため、既定はここに持つ（OwnerTokenBinding.DefaultClients と同じ形）。
/// </summary>
public static class ServiceCallerNames
{
    // リスク管理（REST）
    public const string RiskOpenPositions = "risk-controls.open-positions";
    public const string RiskTradingHalt = "risk-controls.trading-halt";
    public const string RiskEntryBlockers = "risk-controls.entry-blockers";
    public const string RiskWorkingEntryOrders = "risk-controls.working-entry-orders";
    public const string RiskSizingContext = "risk-controls.sizing-context";
    public const string RiskBuyInInferences = "risk-controls.buy-in-inferences";
    public const string RiskSessionUptime = "risk-controls.session-uptime";
    public const string RiskOpeningInventory = "risk-controls.opening-inventory";
    public const string RiskDriftAdoptions = "risk-controls.drift-adoptions";
    public const string RiskFills = "risk-controls.fills";
    public const string RiskStageGate = "risk-controls.stage-gate";
    public const string RiskOwnerSecondFactorVerify = "risk-controls.owner-second-factor.verify";

    // 市場監視・費用統制・前提条件・発注執行・報告書・監査・情報収集（REST）
    public const string MonitorWatchlist = "monitor.watchlist";
    public const string MonitorWatchlistAsOf = "monitor.watchlist.as-of";
    public const string CostState = "costs.state";
    public const string Assumptions = "assumptions";
    public const string OrderExecutionDailyBars = "order-execution.daily-bars";
    public const string OrderExecutionInFlightCloses = "order-execution.in-flight-closes";
    public const string OrderExecutionShortPermit = "order-execution.short-permit";
    public const string ReportsDailyPolicy = "reports.daily-policy";
    public const string ReportsDailyPolicyAsOf = "reports.daily-policy.as-of";
    /// <summary>NFR-06, IADR-0634: 監視銘柄の入れ替えの適用の結び付きの照会（市場監視だけ）。</summary>
    public const string ReportsWatchlistProposalBinding = "reports.watchlist-proposal.binding";
    /// <summary>NFR-06, IADR-0634: 同じ結び付きの予約（1 回だけ。市場監視だけ）。</summary>
    public const string ReportsWatchlistProposalBindingReserve = "reports.watchlist-proposal.binding.reserve";
    public const string AuditEventsByType = "audit.events.by-type";
    public const string CollectionRunOnce = "collection.run-once";

    // east-west gRPC 面（サービスの型ごと。既定は REST で、クラウドは gRPC を結線しない）
    public const string GrpcRiskControlsRead = "grpc.risk-controls-read";
    public const string GrpcWatchlistRead = "grpc.watchlist-read";
    public const string GrpcCostStateRead = "grpc.cost-state-read";
    public const string GrpcAssumptionsRead = "grpc.assumptions-read";
    public const string GrpcOrderExecutionRead = "grpc.order-execution-read";
    public const string GrpcDailyPolicyRead = "grpc.daily-policy-read";
    public const string GrpcAuditEventsRead = "grpc.audit-events-read";

    /// <summary>クライアントの名前の接頭辞（基盤の realm の SERVICE_CLIENTS。<c>ai-stock-trading-svc-&lt;サービス&gt;</c>）。</summary>
    public const string ClientPrefix = "ai-stock-trading-svc-";

    /// <summary>
    /// 口ごとの既定の呼び手（サービスの名前。clientId は <see cref="ClientPrefix"/> ＋ 名前）。一覧に無い口（<see cref="CollectionRunOnce"/> など）はサービスに開かない。
    /// 🔴 描画の検査（scripts/helm-service-auth-clients.test.js）がこの表を `(名前, ["サービス", ...]),` の形で読む。形を変えるなら検査も直す。
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultCallers { get; } = Build(new (string Name, string[] Services)[]
    {
        (RiskOpenPositions, ["information-collection", "market-monitor", "report", "trade-decision"]),
        (RiskTradingHalt, ["order-execution"]),
        (RiskEntryBlockers, ["trade-decision"]),
        (RiskWorkingEntryOrders, ["trade-decision"]),
        (RiskSizingContext, ["trade-decision"]),
        (RiskBuyInInferences, ["report"]),
        (RiskSessionUptime, ["report"]),
        (RiskOpeningInventory, ["report"]),
        (RiskDriftAdoptions, ["report"]),
        (RiskFills, ["report"]),
        (RiskStageGate, ["report"]),
        (RiskOwnerSecondFactorVerify, ["report"]),
        (MonitorWatchlist, ["information-collection", "trade-decision"]),
        (MonitorWatchlistAsOf, ["trade-decision"]),
        (CostState, ["information-collection", "trade-decision"]),
        (Assumptions, ["cost-control", "trade-decision"]),
        (OrderExecutionDailyBars, ["backtest", "trade-decision"]),
        (OrderExecutionInFlightCloses, ["trade-decision"]),
        (OrderExecutionShortPermit, ["risk-management"]),
        (ReportsDailyPolicy, ["trade-decision"]),
        (ReportsDailyPolicyAsOf, ["trade-decision"]),
        (ReportsWatchlistProposalBinding, ["market-monitor"]),
        (ReportsWatchlistProposalBindingReserve, ["market-monitor"]),
        (AuditEventsByType, ["report"]),
        (GrpcRiskControlsRead, ["information-collection", "market-monitor", "order-execution", "report", "trade-decision"]),
        (GrpcWatchlistRead, ["information-collection", "trade-decision"]),
        (GrpcCostStateRead, ["information-collection", "trade-decision"]),
        (GrpcAssumptionsRead, ["cost-control", "trade-decision"]),
        (GrpcOrderExecutionRead, ["backtest", "risk-management", "trade-decision"]),
        (GrpcDailyPolicyRead, ["trade-decision"]),
        (GrpcAuditEventsRead, ["report"]),
    });

    private static Dictionary<string, IReadOnlyList<string>> Build((string Name, string[] Services)[] rows) =>
        rows.ToDictionary(r => r.Name, r => (IReadOnlyList<string>)r.Services.Select(s => ClientPrefix + s).ToArray(), StringComparer.Ordinal);
}
