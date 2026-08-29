namespace RiskManagementService.Hosted;

// FR-20, ADR-0008, IADR-0083: 撤退の定期評価ドライバの構成。既定は安全側（無効・opt-in）。
// 有効化しても既定 StagePerformance（実 DD 未供給・起点 Stage 0）では発火しない＝不活性。実 DD 供給（別 issue）が
// 結線され運用者が明示的に有効化して初めて自動停止が作動する（QuoteRefreshService・#141 リコンサイルと同型）。
public sealed class WithdrawalEvaluationOptions
{
    public const string SectionName = "WithdrawalEvaluation";

    /// <summary>ドライバを起動するか。既定 false（opt-in・安全側）。</summary>
    public bool Enabled { get; set; }

    /// <summary>評価間隔（秒）。既定 300（5 分）。撤退は緩やかな安全チェックのため過頻度にしない。</summary>
    public int IntervalSeconds { get; set; } = 300;
}
