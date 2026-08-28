namespace MarketMonitorService.Domain;

// FR-03, FR-13: 市場監視のソフト設定。変動閾値・クールダウン・監視銘柄。設定ストアで変更できる。
public record MarketMonitorSettings
{
    /// <summary>変動閾値（前回判断時点価格比。0.03 = ±3%）。|変動率| がこれ以上で超過と判定する。</summary>
    public required decimal MovementThresholdRatio { get; init; }

    /// <summary>同一銘柄の再トリガー抑制期間（クールダウン）。乱高下時の過剰取引・LLM 費用暴走を防ぐ。</summary>
    public required TimeSpan Cooldown { get; init; }

    /// <summary>監視対象の銘柄。</summary>
    public required IReadOnlyCollection<MonitoredSymbol> MonitoredSymbols { get; init; }
}
