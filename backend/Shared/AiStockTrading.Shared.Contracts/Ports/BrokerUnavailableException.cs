namespace AiStockTrading.Shared.Contracts.Ports;

// FR-05, ADR-0002/ADR-0024（OpenD 常駐・SPOF）, #331, IADR-0211:
// **注文がブローカーへ届き得ない段階**（接続確立の失敗）で発注が成立しなかったことを表す。
//
// 契約（送出してよい範囲が本型の中核である）:
//   - 送出してよいのは「確実に未発注」と言い切れる失敗だけ（moomoo では EnsureConnectedAsync＝
//     InitConnect 失敗・接続応答タイムアウト・口座列挙失敗）。
//   - **発注送信後の失敗（応答タイムアウト等）に使ってはならない**——届いたか不明であり、
//     受け手は本例外で予約（IADR-0057）を手放す（承認の発注は予約を見送りの終端へ移し、ガード・S1 の決済は
//     解放して次の巡回で撃ち直す）。届いていた注文の予約を手放すと再配送で二重発注（実弾では実損）になる。
//     🔴 #876, IADR-0398: 承認の発注は予約を**削除しない**——削除すると同じ承認の再配送が予約を取り直して
//     発注でき、見送りを受けて在庫を戻した台帳が押さえていない決済が生きる。不明は対になる
//     <see cref="BrokerDispatchIndeterminateException"/> で伝播し、予約は解放も確定もしない
//     （#848・IADR-0117 改定 6 で型を与えた。滞留の解消は同型のコメントを参照——リコンサイルは
//     アプリ既定こそ無効だが配備では有効で、解放だけが門で閉じている。#856 / IADR-0362）。
//   - 🔴 **予約を手放して（解放して撃ち直す／見送りの終端にする）よいのは本例外だけである**（IADR-0117 改定 7）。一括 catch で
//     対の型（届いたか不明）と混ぜて受けると、「未発注と仮定して撃ち直す」側へ倒れる。
//   - 受け手は本例外を OrderStatus.Rejected（証券会社が受理しなかった状態）へ**丸めない**。
//     見送り（OrderDispatchForgone）として記録・通知する（キューイングせず破棄）。
public sealed class BrokerUnavailableException : Exception
{
    public BrokerUnavailableException(string message)
        : base(message)
    {
    }

    public BrokerUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
