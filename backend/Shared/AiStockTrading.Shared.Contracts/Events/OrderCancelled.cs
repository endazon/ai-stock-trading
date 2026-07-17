namespace AiStockTrading.Shared.Contracts.Events;

// FR-05, FR-19, IADR-0067: 発注済み注文が取り消された（注文履歴テレメトリ）。
// 相関キーは既存の注文系イベント（OrderApproved/OrderExecuted）と同じ DecisionId。銘柄・方向は本イベントに持たず、
// 購読側が DecisionId で OrderApproved から補完する（OrderExecuted と同じ設計・IADR-0018）。
// Reason は取消の駆動元（#141 の自動リコンサイル・#152 の pause 強制取消・時限取消）が書き込む自由文字列とする。
// 理由を列挙にすると駆動元の理由体系を本契約が先取りしてしまうため、境界を守って文字列にする（IADR-0067）。
public record OrderCancelled(
    Guid DecisionId,
    string OrderId,
    string Reason,
    DateTimeOffset CancelledAt);
