namespace AiStockTrading.Shared.Contracts.Events;

// FR-20, FR-11, FR-09, UC-06, ADR-0008, IADR-0083: 撤退（差し戻し）基準に到達し、安全側の自動対応が発火した。
// 撤退の定期評価ドライバ（Risk・#166）が、新規に kill switch を自動起動した瞬間にのみ発行する（新規停止＝1 回）。
// 段階の実降格は行わず降格提案（ProposedStage）に留め、確定は承認付き遷移（StageTransitioned・IADR-0082）を要する。
// NotificationService が購読して利用者へ通知（FR-09）し、AuditService が中央監査台帳へ集約する（FR-11: 全イベントの時系列記録）。
// 段階/理由は Risk.Domain の enum（TradingStage/WithdrawalReason）に依存しないよう primitive で表現する
// （Shared.Contracts → Risk.Domain の依存逆転を避ける・IADR-0082 と同型）。段階は数値割当（連続昇順・0..3）と一致。
public record WithdrawalTriggered(
    int ProposedStage,
    string Reason,
    bool HaltNewEntries,
    DateTimeOffset OccurredAt);
