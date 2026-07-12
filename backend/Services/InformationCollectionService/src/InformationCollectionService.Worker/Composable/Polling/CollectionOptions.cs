namespace AiStockTrading.InformationCollection.Worker.Composable.Polling;

// FR-01, FR-13: 収集ポーリングの構成。既定は 30 分（取引判断用・市場開場中）。用途別（報告書=日次）の厳密化は #21 連携で後続。
internal sealed class CollectionOptions
{
    public const string SectionName = "Collection";

    public int PollIntervalSeconds { get; set; } = 1800;
}
