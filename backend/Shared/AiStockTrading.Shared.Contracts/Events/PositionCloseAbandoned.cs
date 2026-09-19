using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-09, FR-10, FR-11, UC-06, #847, IADR-0357: 手仕舞い（Close）の注文が**未約定残を残したまま終わった**。
//
// 手仕舞いは当日注文であり、約定しなければ引け後に失効する。従来は OrderExecuted(Status=Expired) が
// 「約定 Expired 数量0@0」という一般的な Warning になるだけで、**それが手仕舞いだったことも、何株が
// 残ったかも書かれていなかった**。無保護の建玉（ADR-0040 の S2）はそのまま翌日へ持ち越される。
//
// 発行するのは取引台帳（IADR-0018）である —— 承認 Intent（建玉効果・銘柄・方向）と約定累計を持つのは
// 台帳だけであり、OrderExecuted / OrderCancelled はいずれも運ばないためである。
// 終端の記録（MarkTerminal）が**初めて成立したときだけ**発行する（単調・冪等。再配送で撃ち直さない）。
//
// 🔴 本イベントは在庫の押さえに一切関与しない（通知と監査のためだけに存在する）。在庫の解放は
// MarkTerminal が既に済ませている（IADR-0117 改定 1/2/4）。
public record PositionCloseAbandoned(
    Guid DecisionId,
    string Symbol,
    Market Market,
    TradeSide Side,
    int ApprovedQuantity,
    int FilledQuantity,
    int RemainingQuantity,
    OrderStatus TerminalStatus,
    DateTimeOffset AbandonedAt);
