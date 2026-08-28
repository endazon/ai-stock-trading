using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Application.State;

// FR-19, FR-10, FR-11, #425, ADR-0025 決定2, IADR-0165 決定3:
// 自前計数した GFV 発生 1 件（**追記専用**の 1 行）。
//
// **1 行 = 1 注文**である（主キー `OrderId`）。部分約定は同一注文の進行であり 1 件、
// メッセージの再送でも二重計上しない。ADR-0025 が「未決済資金による買付を自らのシステムで記録し、
// その件数を GFV 発生回数として扱う」と定めた「件数」の集計元は**本行の件数**である。
//
// **`RejectionReason.CashAccountSettlementHold` の拒否件数を集計元にしてはならない。**
// 同理由はガードが**働いた**記録（買付を止めた回数）であり、本行はガードが**すり抜けられた**記録である。
// 向きが逆であり、取り違えると「ガードが働くほど GFV が増える」という反転した像になる。
public sealed record GoodFaithViolationRecord(
    Guid Id,
    // ブローカの注文 ID（計上単位＝主キー）。
    string OrderId,
    // 相関する取引判断（発注審査の記録と突き合わせるための鍵）。
    Guid DecisionId,
    string Symbol,
    Market Market,
    // その約定の基準通貨（USD）建て金額。
    decimal PurchaseAmountInBase,
    // 判定に用いた決済済み資金。**null は「供給されていなかった」**（0 ではない）。
    decimal? SettledCashInBase,
    // 計上した取引日（東部時間）。
    DateOnly OccurredOn,
    DateTimeOffset ExecutedAt,
    DateTimeOffset RecordedAt);

// FR-19, FR-10, FR-11, UC-06, #464, ADR-0028 決定1/決定2, IADR-0182:
// GFV 違反記録の**解除**を表す 1 行（**追記専用**）。
//
// 🔴 **解除は「記録を消すこと」ではない。** ADR-0028 決定1 が「GFV 違反記録は失効させない」と定めている。
// 違反記録（`GoodFaithViolationRecord`）は台帳に残り続け、本行はそれとは別に「その記録による**停止**を
// 利用者が解いた」という事実を追記する。監査は**発生と解除の双方**を辿れる。
//
// **主キーは解除対象の `OrderId`**（＝違反記録の計上単位）。これにより
//   - ADR-0028 決定2 が求める「**どの記録に対して**解除したか」が構造的に表現され、
//   - 同じ記録を二重に解除しても件数が狂わない（冪等）。
public sealed record GoodFaithViolationClearance(
    // 解除対象の違反記録（計上単位＝ブローカの注文 ID）。
    string OrderId,
    // 解除した利用者（認証済みトークンの名前）。**空は許さない**（誰が解いたか不明な解除を作らない）。
    string ClearedBy,
    // 解除の理由。ADR-0028 決定2 の「原因の是正が済んでいることの確認を伴う」の記録。**空は許さない**。
    string Reason,
    DateTimeOffset ClearedAt);
