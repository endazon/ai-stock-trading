namespace AiStockTrading.Shared.Contracts.Ports;

// 🔴 FR-05, FR-10, FR-11, UC-06, #848, IADR-0117（2026-09-19 追記・改定 6）:
// **注文を送信した後で、届いたかどうかが確認できなかった**ことを表す（応答待ちのタイムアウト・SDK の応答異常）。
// <see cref="BrokerUnavailableException"/>（接続確立の失敗＝**確実に未発注**）とは**逆側の不明**であり、
// 対になる型として並べて置いてある。
//
// 契約（送出してよい範囲が本型の中核である）:
//   - 送出してよいのは「**送信は済んだが結果が確認できない**」失敗だけ。送信していないと言い切れる失敗
//     （発注前検証で棄却・接続確立の失敗）や、**確認できた非受理**（OpenD が retType != 0 を返した）は対象外。
//   - 受け手は本例外を <c>OrderStatus.Rejected</c> へ**丸めてはならない**。`Rejected` はリスク管理の取引台帳で
//     **在庫の押さえを解く引き金**であり（IADR-0117 決定 3）、状態が分からないまま押さえを解くと
//     同じ建玉に 2 本目の決済が並ぶ（**二重決済で意図しないショート化**）。
//   - 受け手は「**建玉は生じていない**」とも仮定してはならない。エントリーで仮定すると、注文が実際には
//     生きていた場合に**保護レグを張らないまま無保護の建玉**ができる。
//   - 受け手は予約（IADR-0057）を**解放も確定もしない**。Reserved のまま残す。**二重発注を防ぐのは
//     この予約であり、リコンサイルの有無に依らない。** 滞留の解消は、client order id による
//     リコンサイル（IADR-0092 / IADR-0074）が**有効なら**実状態（Placed / NotPlaced / Indeterminate）へ解決する。
//     🔴 **リコンサイルは既定で無効**（Reconciliation:Enabled=false・UseBrokerProbe=false。deploy/ にも上書きは無い）。
//     いまの配備では滞留 Reserved は**自動では解決せず、人が証券会社の画面で確認して解決する**
//     （docs/operations/broker-execution-paths-runbook.md。有効化は #856）。
//   - 🔴 受け手は本例外を**一括 catch（catch (Exception)）で「再試行してよい失敗」として受けてはならない**
//     （IADR-0117 改定 7）。保護逆指値ガードの成行手仕舞いがそう受けていたため、巡回ごとに全数量の成行を
//     1 本ずつ重ねていた。**送る前に決定的な DecisionId を予約し、予約が残っている限り再送しない**こと。
//   - **実在しない注文 ID を捏造しない**（自前採番の終端記録を 7 年保持の台帳へ残さない。#842 と同型）。
public sealed class BrokerDispatchIndeterminateException : Exception
{
    public BrokerDispatchIndeterminateException(string message)
        : base(message)
    {
    }

    public BrokerDispatchIndeterminateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
