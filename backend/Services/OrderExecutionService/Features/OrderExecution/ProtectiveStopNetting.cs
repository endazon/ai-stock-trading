using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution;

// FR-10, ADR-0040 決定1, #820（#826 項目 3 の S1 側）, IADR-0344 決定5-2・決定6: 保護記録 1 件から見た「エントリー方向の建玉残」。
//
// ブローカーの建玉は銘柄単位の純額であり、どのエントリーの建玉かを区別しない。同じ銘柄・方向に**手法の異なる**保護
// （S0 のブローカー逆指値と S1 のソフトウェア逆指値）が併存すると、片方の建玉がもう片方の「建玉あり」を支えてしまう
// （S0 の逆指値が生き残り反対建玉を生む／S1 の決済が S0 の建玉を売る）。そこで**手法の異なる Active 行が主張する数量を差し引く**。
//
// - S0 行が主張する数量＝逆指値の数量（建玉を覆う数量）。
// - S1 行が主張する数量＝エントリーの約定数量（記録が無ければ 0。**S0 の建玉残を過小に見積もって逆指値を取り消す向きへ倒さない**）。
// - 同じ手法どうしは差し引かない（従来どおり銘柄単位の純額。S1 が無い構成では挙動が 1 バイトも変わらない）。
// - S2 は保護記録を持たないため差し引けない（IADR-0344 残余リスク）。
public static class ProtectiveStopNetting
{
    public static int RemainingPositionFor(
        ProtectiveStopOrder stop,
        IReadOnlyList<BrokerPositionSnapshot> snapshot,
        IEnumerable<ProtectiveStopOrder> activeStops,
        IExecutedOrderStore store)
    {
        ArgumentNullException.ThrowIfNull(stop);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(activeStops);
        ArgumentNullException.ThrowIfNull(store);

        var claimedByOtherMechanisms = activeStops
            .Where(other => other.EntryDecisionId != stop.EntryDecisionId
                && other.State == ProtectiveStopState.Active
                && other.Mechanism != stop.Mechanism
                && other.Symbol == stop.Symbol
                && other.Market == stop.Market
                && other.EntrySide == stop.EntrySide)
            .Sum(other => ClaimedQuantity(other, store));

        return Math.Max(0, DirectionalNet(stop, snapshot) - claimedByOtherMechanisms);
    }

    // 建玉スナップショットから「エントリー方向の残数量」を求める。数量は符号付き（+ロング/−ショート・IADR-0118）。
    public static int DirectionalNet(ProtectiveStopOrder stop, IReadOnlyList<BrokerPositionSnapshot> snapshot)
    {
        var net = snapshot
            .Where(p => p.Symbol == stop.Symbol && p.Market == stop.Market)
            .Sum(p => p.Quantity);
        return stop.EntrySide == TradeSide.Buy ? Math.Max(0, net) : Math.Max(0, -net);
    }

    private static int ClaimedQuantity(ProtectiveStopOrder other, IExecutedOrderStore store) =>
        other.IsSoftwareStop
            ? Math.Min(other.Quantity, store.FindByDecisionId(other.EntryDecisionId)?.FilledQuantity ?? 0)
            : other.Quantity;
}
