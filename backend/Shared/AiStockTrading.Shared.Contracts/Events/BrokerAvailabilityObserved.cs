using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-20, FR-05, #385, 06_daytrading-review §4.2, IADR-0150: 発注執行サービスが定期 probe で観測した
// 「その時刻にブローカ（OpenD）へ到達でき、応答が返った」という事実。
//
// **稼働していなかったことは本イベントを発行しないことで表す。** 到達不能・応答異常・構成無効は
// いずれも「発行しない」であり、受け手は沈黙を稼働 0 分として扱う（BrokerPositionsObserved と同じ作法）。
// 稼働していない旨をイベントで表すと、その通知そのものが失われたとき（プロセス断・ネットワーク断）に
// 「停止していたのに停止の記録が無い」＝稼働として計上される状態になり得る。沈黙が安全側に倒れる向きを選ぶ。
//
// 用途は Stage 1 の期間カウント（§4.2「その日の実際の通常取引時間の 50% 以上が稼働していること」）の供給である。

/// <param name="Provider">
/// FR-20, #334, #385, IADR-0142 決定1: 観測した<b>発注先</b>（実際に接続しているアダプタの自己申告）。
/// <b>既定値を与えない</b>——省略できるようにすると、書き忘れが「Stage 1 に算入される側」へ倒れる。
/// </param>
/// <param name="ObservedAt">観測時刻（UTC）。受け手が米国東部時間の取引日・分へ写す（§4.2 の基準時刻）。</param>
/// <param name="CoveredInterval">
/// この観測が<b>遡って保証する最大の時間</b>（＝probe の巡回間隔）。
/// <para>
/// 受け手は「直前の成功観測から本観測まで」がこの長さ以内であるときだけ、その区間を稼働として積む。
/// 超えていれば probe を落としている＝停止の可能性があるため積まない（IADR-0150 決定2）。
/// 受け手側で上限クランプが掛かるため、本値を大きくしても 1 件で 1 日を埋めることはできない。
/// </param>
public record BrokerAvailabilityObserved(
    BrokerProvider Provider,
    DateTimeOffset ObservedAt,
    TimeSpan CoveredInterval);
