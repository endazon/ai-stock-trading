using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, UC-02, ADR-0016 決定2(b), #331, IADR-0210: エントリーと同時（または失効後の再発注）に
// ブローカーへ**保護逆指値**を発注した。
//
// リスク管理は本イベントの CloseIntent を取引台帳の承認行（AppendApproval）へ結線する——
// 逆指値がブローカー側で約定（＝損切り成立）すると、発注執行の約定追跡（IADR-0113）が発行する
// OrderExecuted（StopDecisionId 相関）が台帳の建玉を減らす。**OrderApproved を再利用しない**のは、
// 発注執行自身が OrderApproved を購読しており、同イベントを流すと逆指値レグを二重発注するため
// （IADR-0210 決定 2）。
//
// Attempt は試行番号（1=エントリー同時発注、2 以降=失効後の再発注）。StopDecisionId は
// EntryDecisionId と Attempt から決定的に導出され、再送・再巡回で重複しない。
public record ProtectiveStopPlaced(
    Guid EntryDecisionId,
    Guid StopDecisionId,
    string StopOrderId,
    OrderIntent CloseIntent,
    decimal TriggerPrice,
    int Attempt,
    DateTimeOffset PlacedAt);
