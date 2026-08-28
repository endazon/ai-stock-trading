namespace AiStockTrading.Shared.Contracts.Ports;

// FR-05, ADR-0002/ADR-0024（OpenD 常駐・SPOF）, #331, IADR-0211:
// **注文がブローカーへ届き得ない段階**（接続確立の失敗）で発注が成立しなかったことを表す。
//
// 契約（送出してよい範囲が本型の中核である）:
//   - 送出してよいのは「確実に未発注」と言い切れる失敗だけ（moomoo では EnsureConnectedAsync＝
//     InitConnect 失敗・接続応答タイムアウト・口座列挙失敗）。
//   - **発注送信後の失敗（応答タイムアウト等）に使ってはならない**——届いたか不明であり、
//     受け手（発注執行）は本例外で予約（IADR-0057）を解放する。届いていた注文の予約を解放すると
//     再配送で二重発注（実弾では実損）になる。不明は従来どおり例外をそのまま伝播し、
//     予約とリコンサイル（IADR-0092）に委ねる。
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
