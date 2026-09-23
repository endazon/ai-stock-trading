namespace MarketMonitorService.Hosted;

// FR-03: ポーリングの構成。監視間隔は市況データ源のレート制限から逆算して設定する（#81/#158・IADR-0068 連携。既定 60s）。
public sealed class MonitorOptions
{
    public const string SectionName = "Monitor";

    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// FR-10, #902, IADR-0365 決定2: 損切り評価の生存要約（Information）を出す最短間隔（秒）。保有を評価している間だけ、
    /// この間隔に 1 回まで出す（初回は即時）。1 未満は 1 として扱う。
    /// </summary>
    public int StopLossSummaryIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// FR-10, #902, IADR-0365 決定3: 保有銘柄の価格がこの秒数を超えて取れないとき Warning を出す（決済はしない）。
    /// 1 未満は 1 として扱う。
    /// </summary>
    public int QuoteMissingWarningSeconds { get; set; } = 300;
}
