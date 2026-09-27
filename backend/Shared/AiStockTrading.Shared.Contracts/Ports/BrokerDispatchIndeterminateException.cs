namespace AiStockTrading.Shared.Contracts.Ports;

// 🔴 FR-05, FR-10, FR-11, UC-06, #848, IADR-0117（2026-09-19 追記・改定 6）:
// **注文を送信した後で、届いたかどうかが確認できなかった**ことを表す（応答待ちのタイムアウト・SDK の応答異常）。
// <see cref="BrokerUnavailableException"/>（接続確立の失敗＝**確実に未発注**）とは**逆側の不明**であり、
// 対になる型として並べて置いてある。
//
// 契約（送出してよい範囲が本型の中核である）:
//   - 送出してよいのは「**送信は済んだが結果が確認できない**」失敗だけ。送信していないと言い切れる失敗
//     （発注前検証で棄却・接続確立の失敗）や、**確認できた非受理**（OpenD が**返事として**失敗を返した＝
//     moomoo では retType == -1 だけ）は対象外。
//     🔴 #848, IADR-0117（改定 8）: **「非成功の応答」は確認できた非受理とは限らない。** moomoo の retType の
//     -100（TimeOut）/ -200 / -400 / -500（Invalid）は「返事を読めなかった」を SDK が応答の形に包んだ値であり、
//     **本例外の対象である**（-100 は送信済み要求の 12 秒打ち切りで、既定構成の返信待ちタイムアウトはこの形で来る）。
//   - 受け手は本例外を <c>OrderStatus.Rejected</c> へ**丸めてはならない**。`Rejected` はリスク管理の取引台帳で
//     **在庫の押さえを解く引き金**であり（IADR-0117 決定 3）、状態が分からないまま押さえを解くと
//     同じ建玉に 2 本目の決済が並ぶ（**二重決済で意図しないショート化**）。
//   - 受け手は「**建玉は生じていない**」とも仮定してはならない。エントリーで仮定すると、注文が実際には
//     生きていた場合に**保護レグを張らないまま無保護の建玉**ができる。
//   - 受け手は予約（IADR-0057）を**解放も確定もしない**。Reserved のまま残す。**二重発注を防ぐのは
//     この予約であり、リコンサイルの有無に依らない。** 滞留の解消は、client order id による
//     リコンサイル（IADR-0092 / IADR-0074）が実状態（Placed / NotPlaced / Indeterminate）へ解決する。
//     🔴 #856, IADR-0362（2026-09-19）: **アプリの既定は無効のままだが、配備では有効である**
//     （deploy/helm/ai-stock-trading/values.yaml の Reconciliation__Enabled / __UseBrokerProbe＝true）。
//     ただし**解放（NotPlaced → 予約の削除）だけは門が閉じている**（#1051, IADR-0444: 取引環境ごとの
//     Reconciliation__ReleaseOnNotPlaced__Simulate / __Real がどちらも false）
//     ——解放は再発注の許可であり、誤判定は二重発注に直結するため、取引環境ごとに実機の記録で基準を満たすまで開けない（ADR-0045）。
//     したがって配備でいま自動解決するのは **Placed 側（＋記録ありの自己修復）だけ**であり、
//     NotPlaced と Indeterminate は据え置かれて人が証券会社の画面で確認する
//     （docs/operations/broker-execution-paths-runbook.md）。
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
