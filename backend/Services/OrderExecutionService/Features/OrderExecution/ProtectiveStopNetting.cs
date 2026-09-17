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
    public static int DirectionalNet(ProtectiveStopOrder stop, IReadOnlyList<BrokerPositionSnapshot> snapshot) =>
        DirectionalNet(stop.Symbol, stop.Market, stop.EntrySide, snapshot);

    /// <summary>銘柄・市場・エントリー方向から「その方向の建玉残」を求める（行を持たない呼び出し用）。</summary>
    public static int DirectionalNet(
        string symbol, Market market, TradeSide entrySide, IReadOnlyList<BrokerPositionSnapshot> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var net = snapshot
            .Where(p => p.Symbol == symbol && p.Market == market)
            .Sum(p => p.Quantity);
        return entrySide == TradeSide.Buy ? Math.Max(0, net) : Math.Max(0, -net);
    }

    /// <summary>
    /// FR-10, ADR-0040 決定1（S1）, #820 の監査（売り過ぎの防止）, IADR-0344 決定5-2:
    /// 同じ銘柄・市場・方向の<b>ソフトウェア逆指値（S1）へ建玉残を配分する</b>。
    /// <para>
    /// 🔴 <b>行ごとに「建玉残」を上限にすると売り過ぎる。</b>ブローカーの建玉は銘柄単位の純額であり、
    /// N 件の S1 行がそれぞれ全量を自分の持ち分と見なすため、手動決済などで建玉が減った後に合計が保有を超える
    /// （監査で実測: 10 株の S1 行 2 件＋手動売却 5 株 → 20 株の決済 vs 保有 15 株。反対建玉＝空売りになる）。
    /// </para>
    /// <para>
    /// 配分は<b>決定的</b>である——到達時刻（未到達なら記録の作成時刻）→作成時刻→EntryDecisionId の順に古い行から
    /// 「その行のエントリー約定数量」を上限として割り当て、残りを次の行へ回す。ハンドラとガードのどちらから
    /// 呼んでも同じ配分になる（並行実行でも合計が建玉を超えない）。S0 が主張する数量は先に差し引く。
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<Guid, int> AllocateSoftwareStops(
        string symbol,
        Market market,
        TradeSide entrySide,
        IReadOnlyList<BrokerPositionSnapshot> snapshot,
        IEnumerable<ProtectiveStopOrder> activeStops,
        IExecutedOrderStore store,
        int pendingCloseQuantity = 0)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(activeStops);
        ArgumentNullException.ThrowIfNull(store);

        var group = activeStops
            .Where(s => s.State == ProtectiveStopState.Active
                && s.Symbol == symbol && s.Market == market && s.EntrySide == entrySide)
            .ToList();

        // S0（ブローカー側逆指値）が覆っている数量は S1 の持ち分ではない（従来の RemainingPositionFor と同じ向き）。
        // 🔴 さらに、**発注済みで未約定の決済**も差し引く。建玉照会は決済が約定するまで減らないため、差し引かないと
        // 同じ建玉を次の行へもう一度配分してしまう（実測: 建玉 15 株に対し 10 株＋10 株＝20 株の決済になった）。
        var budget = Math.Max(
            0,
            DirectionalNet(symbol, market, entrySide, snapshot)
                - group.Where(s => !s.IsSoftwareStop).Sum(s => s.Quantity)
                - Math.Max(0, pendingCloseQuantity));

        var allocation = new Dictionary<Guid, int>();
        // 🔴 順序は**記録の作成時刻**（＝エントリーの発注順）で決める。到達時刻を混ぜると、1 回の到達を処理する途中で
        // 行に到達を書き込むたびに順序が変わり、どの行にも持ち分が回らない（実測: 2 件とも据え置きになった）。
        foreach (var row in group
            .Where(s => s.IsSoftwareStop)
            .OrderBy(s => s.CreatedAt)
            .ThenBy(s => s.EntryDecisionId))
        {
            var filled = Math.Min(row.Quantity, store.FindByDecisionId(row.EntryDecisionId)?.FilledQuantity ?? 0);
            var allowance = Math.Min(Math.Max(0, filled), budget);
            allocation[row.EntryDecisionId] = allowance;
            budget -= allowance;
        }

        return allocation;
    }

    private static int ClaimedQuantity(ProtectiveStopOrder other, IExecutedOrderStore store) =>
        other.IsSoftwareStop
            ? Math.Min(other.Quantity, store.FindByDecisionId(other.EntryDecisionId)?.FilledQuantity ?? 0)
            : other.Quantity;
}
