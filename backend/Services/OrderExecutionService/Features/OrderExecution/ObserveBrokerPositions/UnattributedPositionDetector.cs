using OrderExecutionService.Common.Abstractions;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution.ObserveBrokerPositions;

// 🔴 FR-10, UC-02, ADR-0040 決定1（S1）, #880, IADR-0412 決定1: 建玉観測の常駐（BrokerPositionSnapshotService）が
// **既に取った建玉スナップショット**で、帰属不明の建玉（どの保護記録も主張していない建玉）を検知する。
//
// 常駐ガードは「巡回対象（Active な行）が 0 件なら建玉を照会しない」不変条件を持つため、
// 受理後に 0 約定で取り消された決済の残りがその口座で唯一の S1 の痕跡なら、ガードからは検知が一度も走らない
// （#820 の 11 巡目監査 NB-2。1 銘柄しか持たない PoC 口座で無音）。建玉観測の常駐は保護記録の有無に依らず照会するので、
// そこへ相乗りすれば **OpenD への往復を 1 回も増やさずに**塞げる。
//
// 検知の本体は ProtectiveStopNetting.DetectUnattributedPositions（ガードと同じ純粋な関数）。売らない・記録も作らない・
// 主張も動かさない（通知済みの印だけを楽観並行で書く）。ガードと両方から呼んでも、同じ状態では印の門で 1 回しか鳴らない。
//
// 巡回ごとのスコープで作る（EF のストアが scoped のため）。moomoo 構成でだけ登録する（建玉照会を持つ構成だけ）。
public sealed class UnattributedPositionDetector(
    IProtectiveStopOrderStore stops,
    IExecutedOrderStore store,
    IClock clock,
    int batchSize)
{
    /// <summary>
    /// 照会<b>できた</b>スナップショット（null ではない＝建玉なしの空列を含む）で検知し、発行すべきイベントを返す。
    /// 照会不能（null）では呼ばない——不明を「建玉なし」と読むと通知済みの印を消してしまう（IADR-0412 決定3）。
    /// </summary>
    public IReadOnlyList<object> Detect(IReadOnlyList<BrokerPositionSnapshot> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var events = new List<object>();
        ProtectiveStopNetting.DetectUnattributedPositions(
            snapshot, stops.FindActive(batchSize), stops, store, clock.UtcNow, events);
        return events;
    }
}
