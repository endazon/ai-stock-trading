using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// 🔴 FR-10, FR-11, FR-05, #1048（利用者裁定 2026-10-02・Q3）, IADR-0481 決定3: 発注執行の約定追跡（OrderFillPoller）が、
// **追跡上限（既定 24 時間）を過ぎても非終端のままの注文の追跡を打ち切った**事実。
//
// 打ち切った注文には、以後**終端の約定記録（OrderExecuted）が台帳へ届かない**。台帳の読みが「最後に観測した状態のまま」で
// 止まることを、黙らせずに残す。とくに S2（逆指値なしの建玉）の免除（ProtectiveStopWaived）は終端の約定記録でしか
// 打ち消し・数量の確定（ProtectiveStopWaiverSettled）が付かないため、打ち切られると**発注数量のまま残る**——
// 相関（DecisionId）に本記録が並ぶことで、その状態を追跡できる。
//
// - DecisionId は注文の相関（エントリーなら免除・保護レグと同じ相関）。OrderId はブローカー注文 ID。
// - TrackedFrom は**打ち切った追跡の起点**（記録の ExecutedAt）。同じ注文の追跡が後から起点を進めて窓へ戻され
//   （IADR-0406 決定3 の RenewTracking）、再び期限を過ぎたときは**別の打ち切り**として別に記録する（起点が違う）。
// - LastStatus / FilledQuantity は打ち切った時点で記録が持っていた最後の観測（打ち切りの直前に 1 回だけ照会し直した結果を含む）。
// - Provider は照会したアダプタの発注先（OrderExecuted と同じ出どころ）。
// - 発行は**少なくとも 1 回**（発行の後に印を書く）。受け手（監査台帳）は OrderId と TrackedFrom から決定的に導いた Id で畳む。
public record OrderFillTrackingAbandoned(
    Guid DecisionId,
    string OrderId,
    string Symbol,
    Market Market,
    TradeSide Side,
    PositionEffect PositionEffect,
    int Quantity,
    int FilledQuantity,
    OrderStatus LastStatus,
    DateTimeOffset TrackedFrom,
    TimeSpan MaxTracking,
    BrokerProvider Provider,
    DateTimeOffset AbandonedAt);
