namespace RiskManagementService.Hosted;

// FR-20, FR-10, ADR-0008, IADR-0103, #164: 実DD（観測最大ドローダウン）供給ドライバの構成。既定は安全側（無効・opt-in）。
// 有効化しても時価評価が既定無効（MarketData:EnableMarkToMarket=false・IADR-0066）のうちは DrawdownRatio が常に 0 で
// 供給値が変わらないため書き込みも起きない＝不活性。撤退の実行側（WithdrawalEvaluation:Enabled・IADR-0083）も
// 既定無効であり、自動停止までには 3 つの明示的な有効化を要する（QuoteRefreshService・#141 リコンサイルと同型）。
public sealed class ObservedDrawdownRefreshOptions
{
    public const string SectionName = "ObservedDrawdownRefresh";

    /// <summary>ドライバを起動するか。既定 false（opt-in・安全側）。</summary>
    public bool Enabled { get; set; }

    /// <summary>サンプリング間隔（秒）。既定 300（5 分）。撤退は緩やかな安全チェックのため過頻度にしない。</summary>
    public int IntervalSeconds { get; set; } = 300;
}
