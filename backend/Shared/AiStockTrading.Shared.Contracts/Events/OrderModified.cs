namespace AiStockTrading.Shared.Contracts.Events;

// FR-05, FR-19, IADR-0067: 発注済み注文が訂正された（数量・価格の変更。注文履歴テレメトリ）。
// 相関キーは既存の注文系イベント（OrderApproved/OrderExecuted）と同じ DecisionId。銘柄・方向は本イベントに持たず、
// 購読側が DecisionId で OrderApproved から補完する（OrderExecuted と同じ設計・IADR-0018）。
// 訂正前後の値を両方持つ。監査台帳（FR-11）でイベント単体から「何がどう変わったか」を読めるようにするため。
// 過剰訂正の検知（FR-19）は本イベントの発生回数を購読側で数える（IADR-0040 の AmendmentCount）。
public record OrderModified(
    Guid DecisionId,
    string OrderId,
    int PreviousQuantity,
    decimal PreviousPrice,
    int Quantity,
    decimal Price,
    string Reason,
    DateTimeOffset ModifiedAt);
