using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, FR-11, UC-06, ADR-0007, #292, IADR-0117: 利用者（owner）が建玉の手仕舞いを要求した。
//
// 後続の OrderApproved と同一の DecisionId で相関する。OrderApproved は**アクターも理由も持たない**ため、
// 本イベントが無いと「誰が・なぜ建玉を落としたか」が監査台帳に残らない（FR-11）。
// Side は建玉方向の反対売買（サーバが決める）、Quantity・Price は承認された決済注文の数量・指値。
public record PositionCloseRequested(
    Guid DecisionId,
    string Symbol,
    Market Market,
    TradeSide Side,
    int Quantity,
    decimal Price,
    string Actor,
    string Reason,
    DateTimeOffset RequestedAt);
