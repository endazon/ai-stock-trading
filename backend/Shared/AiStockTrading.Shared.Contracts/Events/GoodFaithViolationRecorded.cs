using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-19, FR-10, FR-11, UC-06, #425, ADR-0025 決定2, ADR-0021 決定4-2/4-3, IADR-0166:
// **未決済資金による買付を約定として観測し、GFV 発生 1 件として記録した。**
//
// ★★ この件数が何であるかを取り違えないこと（ADR-0025 §理由 が明記した限界） ★★
//
//   自前で数えられるのは**自らのガードをすり抜けた買付**だけであり、**ブローカー側が独自に GFV と判定した
//   事象は捕捉できない**。したがって本件数は「**ブローカーの GFV カウンタの写し**」ではなく
//   「**自らのガードの失敗回数**」である。**両者が一致する保証はない。**
//
// **ガードが正しく働けば本イベントは 1 件も発行されない。** 発行された時点で、発注前の GFV 回避ガード
// （`CashAccountSettlementHold`）をすり抜けた買付が現に約定したということであり、
// **ガードの不具合または口座観測の欠落を示す。** 監査ログ（FR-11）に残すのはそのためである
// （手入力を採らなかった理由の 1 つが「監査証跡に乗らない」ことであった）。
public record GoodFaithViolationRecorded(
    Guid EventId,
    // 相関する取引判断。発注審査の記録（OrderRejected / OrderApproved）と突き合わせて、
    // 「なぜガードが拒否しなかったのか」を事後に再構成するための鍵である。
    Guid DecisionId,
    // ブローカの注文 ID。**計上単位（1 注文 1 件）そのもの**であり、追記台帳の主キーでもある
    // （部分約定の進行・メッセージ再送で二重計上しない）。
    string OrderId,
    string Symbol,
    Market Market,
    // その約定の基準通貨（USD）建て金額（＝未決済資金で買った疑いのある金額）。
    decimal PurchaseAmountInBase,
    // 判定に用いた決済済み資金。**null は「供給されていなかった」を意味する**——0 ではない
    // （0 と書くと「残高が 0 だった」と読まれる。#424 の表示規約と同じ区別）。
    decimal? SettledCashInBase,
    // 計上した取引日（東部時間）。期間集計の単位。
    DateOnly OccurredOn,
    DateTimeOffset ExecutedAt,
    DateTimeOffset RecordedAt);
