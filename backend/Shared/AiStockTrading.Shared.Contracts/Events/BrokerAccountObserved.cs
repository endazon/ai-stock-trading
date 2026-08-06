using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-19, FR-10, #375, ADR-0021 決定3, IADR-0153: 発注執行サービスが定期 probe で観測した
// 「その時刻にブローカーへ照会でき、口座種別が判明した」という事実。
//
// **判明しなかったことは本イベントを発行しないことで表す。** 到達不能・応答異常・種別不明（TrdAccType_Unknown）は
// いずれも「発行しない」であり、受け手は沈黙を「口座種別が不明」として扱う（BrokerAvailabilityObserved と同じ作法）。
// 「不明である」をイベントで表すと、その通知そのものが失われたとき（プロセス断・ネットワーク断）に
// **不明が届かないまま古い観測が生き残る**——沈黙が安全側に倒れる向きを選ぶ。
//
// 用途は口座種別に依存する統制（ADR-0021 決定4 の 5 統制）の供給である。

/// <param name="Provider">
/// 観測した<b>発注先</b>（実際に接続しているアダプタの自己申告。IADR-0142 決定1 と同じ出どころ）。
/// <b>既定値を与えない</b>——省略できるようにすると、書き忘れが「統制を緩める側」へ倒れる。
/// </param>
/// <param name="Account">
/// 照会で得た口座の状態。<b>この値が届いたこと自体が「照会に成功した」ことを意味する</b>。
/// </param>
/// <param name="ObservedAt">観測時刻（UTC）。受け手が観測の新旧を判定する。</param>
public record BrokerAccountObserved(
    BrokerProvider Provider,
    BrokerAccountState Account,
    DateTimeOffset ObservedAt);
