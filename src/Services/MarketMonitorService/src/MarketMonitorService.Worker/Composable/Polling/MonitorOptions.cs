namespace AiStockTrading.MarketMonitor.Worker.Composable.Polling;

// FR-03: ポーリングの構成。監視間隔は moomoo の購読枠・レート制限から逆算して設定する（#13 連携で確定。既定 60s）。
internal sealed class MonitorOptions
{
    public const string SectionName = "Monitor";

    public int PollIntervalSeconds { get; set; } = 60;
}
