namespace MarketMonitorService.Domain;

// FR-03, FR-13, FR-17: 市場監視の既定設定（全体前提条件 05_trading-assumptions・ワークフロー 02 準拠）。
// 実運用では設定ストアから読み込む。本クラスは初期値を提供する。
public static class MonitorDefaults
{
    /// <summary>変動閾値 ±3%（ワークフロー 02 の例・前回判断時点価格比）。</summary>
    public const decimal MovementThresholdRatio = 0.03m;

    /// <summary>クールダウン 15 分（同一銘柄の再トリガー抑制。ワークフロー 02）。</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);

    public static MarketMonitorSettings CreateSettings(
        IReadOnlyCollection<MonitoredSymbol>? monitoredSymbols = null) => new()
        {
            MovementThresholdRatio = MovementThresholdRatio,
            Cooldown = Cooldown,
            MonitoredSymbols = monitoredSymbols ?? [],
        };
}
