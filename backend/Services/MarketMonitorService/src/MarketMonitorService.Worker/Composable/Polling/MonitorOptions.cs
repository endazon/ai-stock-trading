namespace AiStockTrading.MarketMonitor.Worker.Composable.Polling;

// FR-03: ポーリングの構成。監視間隔は市況データ源のレート制限から逆算して設定する（#81/#158・IADR-0068 連携。既定 60s）。
internal sealed class MonitorOptions
{
    public const string SectionName = "Monitor";

    public int PollIntervalSeconds { get; set; } = 60;
}
