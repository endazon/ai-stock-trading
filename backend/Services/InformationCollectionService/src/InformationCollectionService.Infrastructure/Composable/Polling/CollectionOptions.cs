namespace AiStockTrading.InformationCollection.Infrastructure.Composable.Polling;

// #121, FR-02, IADR-0023: 取引サイクルの定時トリガ方式。
// InProcess（既定）: dev/単体の in-process ポーリング（現行・IADR-0023）。
// External: 本番スケジューラ（K8s CronJob）駆動。in-process 巡回は停止し、run-once エンドポイントで 1 巡回を起動する。
internal enum CollectionTrigger
{
    InProcess,
    External
}

// FR-01, FR-13: 収集ポーリングの構成。既定は 30 分（取引判断用・市場開場中）。用途別（報告書=日次）の厳密化は #21 連携で後続。
internal sealed class CollectionOptions
{
    public const string SectionName = "Collection";

    public int PollIntervalSeconds { get; set; } = 1800;

    // #121: 定時トリガ方式（fail-safe: 未設定=InProcess で現行の in-process ポーリングを維持）。
    // 本番は Collection:Trigger=External とし、K8s CronJob が run-once を叩いてサイクルを起動する。
    public CollectionTrigger Trigger { get; set; } = CollectionTrigger.InProcess;
}
