using AiStockTrading.Shared.Contracts.Events;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Messaging;

/// <summary>
/// 🔴 NFR, FR-10, IADR-0627 決定 1: <b>発行元を受け手で確かめる型の表（単一の情報源）。</b>
/// <para>
/// 受け手の判断・統制・発注の入力になるメッセージ型ごとに、<b>発行してよいサービス</b>（発行元）と、
/// <b>確かめる受け手</b>を宣言する。発行元はこの表の型を出すときに印（HMAC）を付け、受け手は表の型を処理する前に
/// 発行元の許可と印の両方を確かめる（<see cref="MessageOriginVerifier"/>）。
/// </para>
/// <para>
/// 🔴 <b>受け手に型を足したら、ここにも足す。</b> <c>MessageOriginCatalogCompletenessTests</c> がハンドラの受ける型を
/// ソースから走査し、表（と <see cref="ExcludedReceivers"/>）と突き合わせる（足し忘れで赤）。
/// </para>
/// </summary>
public static class MessageOriginCatalog
{
    // 発行元の識別子。サービス名（ai-stock-trading.&lt;名前&gt;-service）の &lt;名前&gt; と同じ（IssuerIdFor）。
    public const string TradeDecision = "trade-decision";
    public const string OrderExecution = "order-execution";
    public const string MarketMonitor = "market-monitor";
    public const string RiskManagement = "risk-management";
    public const string InformationCollection = "information-collection";
    public const string Backtest = "backtest";
    public const string Report = "report";
    public const string CostControl = "cost-control";

    private static MessageOriginRule Rule<T>(string[] issuers, string[] receivers) =>
        new(typeof(T), issuers, receivers);

    /// <summary>表の全行。型の完全名の順に並べる必要はない（試験は集合で比べる）。</summary>
    public static IReadOnlyList<MessageOriginRule> Rules { get; } =
    [
        // 判断: 審査（承認の印つきの発注に至る）と、急変検知の基準値。
        Rule<TradeDecisionMade>([TradeDecision], [RiskManagement, MarketMonitor]),
        Rule<TradeDecisionHeld>([TradeDecision], [MarketMonitor]),
        // 判断の起点と入力: 急変の検知（取引判断を起こす）・収集した情報（判断の材料）。
        Rule<PriceMovementDetected>([MarketMonitor], [TradeDecision]),
        Rule<InformationCollected>([InformationCollection], [TradeDecision]),
        // 損切り到達: ソフトウェア逆指値の建玉を成行で決済する。
        Rule<StopLossTriggered>([MarketMonitor], [OrderExecution]),
        // 情報の縮退: 新規建ての停止の出し入れ。
        Rule<InformationSourceDegraded>([InformationCollection], [RiskManagement]),
        Rule<InformationSourceRecovered>([InformationCollection], [RiskManagement]),
        Rule<InformationSourceStateObserved>([InformationCollection], [RiskManagement]),
        // バックテストの判定: 段階ゲートの昇格の入力。
        Rule<BacktestEvaluated>([Backtest], [RiskManagement]),
        // 口座・建玉・稼働の観測: 基準資金・口座種別・建玉の突き合わせ・段階ゲートの稼働。
        Rule<BrokerAccountObserved>([OrderExecution], [RiskManagement]),
        Rule<BrokerAvailabilityObserved>([OrderExecution], [RiskManagement]),
        Rule<BrokerPositionsObserved>([OrderExecution], [RiskManagement]),
        // 注文の結果: 取引台帳・処理中の決済の押さえ・GFV・段階ゲートの件数・相場操縦の検知。
        Rule<OrderExecuted>([OrderExecution], [RiskManagement]),
        Rule<OrderCancelled>([OrderExecution], [RiskManagement]),
        Rule<OrderModified>([OrderExecution], [RiskManagement]),
        Rule<OrderDispatchForgone>([OrderExecution], [RiskManagement]),
        Rule<ProtectiveStopPlaced>([OrderExecution], [RiskManagement]),
        Rule<ProtectiveStopCoverageLost>([OrderExecution], [RiskManagement]),
        Rule<SoftwareStopExecuted>([OrderExecution], [RiskManagement]),
        Rule<SoftwareStopLineWidened>([OrderExecution], [RiskManagement]),
        // 注文の承認: 発注執行は承認の印（IADR-0610）に加えてこちらも確かめる。リスク管理は自分の発行を台帳へ取り込む。
        Rule<OrderApproved>([RiskManagement], [RiskManagement, OrderExecution]),
        // 手仕舞いの取消要求・乖離の取り込み: 発注執行がブローカの注文を取り消す・保護レグを合わせる。
        Rule<PositionCloseCancellationRequested>([RiskManagement], [OrderExecution]),
        Rule<PositionDriftAdopted>([RiskManagement], [OrderExecution]),
        // LLM の費用: 月次上限（費用の統制）の台帳。
        Rule<LlmCostIncurred>([TradeDecision, Report], [CostControl]),
    ];

    /// <summary>
    /// 表の型を受けるが<b>確かめない</b>受け手（サービス, 型）と理由。監査（記録だけ）と通知（利用者へ知らせるだけ）は
    /// 統制の入力にならないので、サービスごと対象外（<see cref="ExcludedServices"/>）。
    /// </summary>
    public static IReadOnlyList<(string Service, Type MessageType, string Reason)> ExcludedReceivers { get; } =
    [
        (RiskManagement, typeof(StopLossTriggered), "検知をログに残すだけで、何も発行・記録しない（決済は発注執行が持つ）"),
        (CostControl, typeof(AssumptionsChanged), "キャッシュを捨てるだけで本文の値を使わない（版は REST で取り直す）"),
    ];

    /// <summary>受ける型をすべて対象外にするサービスと理由。</summary>
    public static IReadOnlyList<(string Service, string Reason)> ExcludedServices { get; } =
    [
        ("audit", "受けた事実を記録するだけで、判断・統制・発注に使わない"),
        ("notification", "利用者へ知らせるだけで、判断・統制・発注に使わない（操作は認可つきの REST / gRPC を通る）"),
    ];

    /// <summary>型の行。無ければ null（表の外の型は印を付けず・確かめない）。</summary>
    public static MessageOriginRule? Find(Type messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        foreach (var rule in Rules)
        {
            if (rule.MessageType == messageType)
                return rule;
        }

        return null;
    }

    /// <summary>
    /// サービス名（例 <c>ai-stock-trading.trade-decision-service</c>）から発行元の識別子（<c>trade-decision</c>）を導く。
    /// </summary>
    public static string IssuerIdFor(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        const string prefix = "ai-stock-trading.";
        const string suffix = "-service";
        if (!serviceName.StartsWith(prefix, StringComparison.Ordinal) || !serviceName.EndsWith(suffix, StringComparison.Ordinal)
            || serviceName.Length <= prefix.Length + suffix.Length)
        {
            throw new ArgumentException(
                $"サービス名は ai-stock-trading.<名前>-service の形です（値: {serviceName}）。", nameof(serviceName));
        }

        return serviceName[prefix.Length..^suffix.Length];
    }

    /// <summary>このサービスが発行元になる型。</summary>
    public static IReadOnlyList<MessageOriginRule> IssuedBy(string issuerId) =>
        [.. Rules.Where(r => r.Issuers.Contains(issuerId, StringComparer.Ordinal))];

    /// <summary>このサービスが確かめる型。</summary>
    public static IReadOnlyList<MessageOriginRule> VerifiedBy(string issuerId) =>
        [.. Rules.Where(r => r.Receivers.Contains(issuerId, StringComparer.Ordinal))];
}

/// <summary>NFR, IADR-0627 決定 1: 表の 1 行（型・発行してよいサービス・確かめる受け手）。</summary>
public sealed record MessageOriginRule(Type MessageType, IReadOnlyList<string> Issuers, IReadOnlyList<string> Receivers);
