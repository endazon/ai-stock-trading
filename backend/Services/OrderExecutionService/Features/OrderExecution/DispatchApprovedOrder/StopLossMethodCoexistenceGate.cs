using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;

// 🔴 FR-10, ADR-0040 決定1, #1048（利用者裁定 2026-10-02・Q2 案 b）, IADR-0481 決定1: 同じ銘柄・同じ方向に、
// **別の損切りの実行機構の有効な保護記録**が残っているかを判定する純関数。**副作用を持たない**（読み出しも見送りも呼び出し側）。
//
// なぜ要るか: 保護記録の持ち分（ProtectiveStopNetting）はブローカーの建玉照会＝銘柄単位の純額からしか測れない。
// 手法の違う建玉が同じ銘柄に併存すると、S0 の側からは S2 の建玉を差し引けず（S2 は記録を持たない）、
// S0 と S3 は互いの主張を差し引き合って建玉残を 0 と読み得る（両方の逆指値を取り消す）。差し引きの規則を直すのではなく
// **併存そのものを作らない**（新しい手法での新規建てを見送る。決済は止めない）。
//
// 判定の規律:
//   - 「有効」＝完了していない記録（Active と、送信結果待ちの AwaitingEntry）。AwaitingEntry を含めるのは、
//     届いたか不明のまま突合を待つ S0 / S3 の建玉が実在し得るためである（倒れ方は「見送り」＝安全側）。
//   - 自分の記録（同じ EntryDecisionId）は数えない（S1 は送る前に自分の行を残し、S0 / S3 は予約の後に AwaitingEntry を残す）。
//   - 同じ手法の記録は数えない（同じ手法の建玉どうしは従来の規則で扱える）。
//   - S0 の未実装の手法の読み替え（NotImplementedFallbackToBrokerStop）は S0 として扱う（記録の機構も S0 になる）。
//   - S2（記録を持たない）の建玉はここでは見えない。S2 の建玉は帰属不明の建玉としてしか現れず、
//     その判定は発注執行の HasUnattributedPositionAsync が担う（IADR-0481 決定2）。
public static class StopLossMethodCoexistenceGate
{
    /// <summary>
    /// 手法の解決結果から、その新規建てが持つ（または持たない）保護記録の機構を返す。見送り（Refused）は null。
    /// </summary>
    public static StopLossExecutionMethod? MechanismOf(StopLossMethodDisposition disposition) => disposition switch
    {
        StopLossMethodDisposition.BrokerStopOrder => StopLossExecutionMethod.BrokerStopOrder,
        StopLossMethodDisposition.NotImplementedFallbackToBrokerStop => StopLossExecutionMethod.BrokerStopOrder,
        StopLossMethodDisposition.SoftwareStop => StopLossExecutionMethod.SoftwareStop,
        StopLossMethodDisposition.ProtectiveStopWaived => StopLossExecutionMethod.NoProtectiveStop,
        StopLossMethodDisposition.AlternativeBrokerOrderType => StopLossExecutionMethod.AlternativeBrokerOrderType,
        _ => null,
    };

    /// <summary>
    /// 新規建て（<paramref name="intent"/>・機構 <paramref name="mechanism"/>）と併存してはならない記録を返す（空なら建ててよい）。
    /// </summary>
    public static IReadOnlyList<ProtectiveStopOrder> Conflicting(
        Guid entryDecisionId,
        OrderIntent intent,
        StopLossExecutionMethod mechanism,
        IEnumerable<ProtectiveStopOrder> rows)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(rows);

        return rows
            .Where(r => r.EntryDecisionId != entryDecisionId
                && r.State != ProtectiveStopState.Completed
                && r.Mechanism != mechanism
                && r.Symbol == intent.Symbol
                && r.Market == intent.Market
                && r.EntrySide == intent.Side)
            .ToList();
    }
}
