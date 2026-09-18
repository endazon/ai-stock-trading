using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution;

// FR-10, ADR-0040 決定1, #820（#826 項目 3 の S1 側）, IADR-0344 決定5-2・決定6・追記(4): 保護記録の「持ち分」。
//
// 🔴 **持ち分を毎巡回ゼロから計算し直さない。** ブローカーの建玉照会は銘柄単位の純額であり、どの建玉がどの保護記録の
// ものかを区別しない。それを毎回引き直して持ち分を決めると、規則をどう変えても「売り過ぎ（反対建玉）」か
// 「損切りが黙って出ない」のどちらかへ倒れる（#820 で 4 巡連続して実測された）。
// そこで **S1 の行は自分の残保護数量（ProtectiveStopOrder.RemainingProtected）を状態として持つ**。
// ここが動かすのは次の 2 つだけである。
//
//   1. **確定**（ReconcileShares）— エントリーの発注記録が終端になった時点で、残保護数量＝エントリーの約定数量。
//   2. **外部要因の減少の一度きりの割り当て**（ReconcileShares）— 人手決済・強制決済・S0 の逆指値の約定などで
//      建玉が減ったぶんを、**観測した時点で 1 回だけ**削り、**保存して記録する**（次の巡回で引き直さない）。
//      削る順は **帳簿だけの行（S1）→ 実注文を持つ行（S0）**、それぞれの中で作成時刻 → EntryDecisionId である
//      （#820 の 5 巡目監査。作成時刻だけで決めると、生きた S0 の逆指値が 0 にされて取り消される）。
//      削りは **2 巡回連続で観測してから確定**し、確定時に 1 回だけ通知する（建玉照会は 1 巡回だけ過少に返り得る）。
//
// 自分が出した決済による減算は SoftwareStopExecutor が行う（決定的な SoftwareCloseDecisionId の試行と 1:1）。
public static class ProtectiveStopNetting
{
    /// <summary>
    /// #820 の 5 巡目監査, IADR-0344 追記(5): 外部要因による減少を<b>確定</b>するまでに要する連続観測回数。
    /// 建玉照会は銘柄単位の純額でしかなく、<b>1 巡回だけ過少に返り得る</b>——1 回の観測で行を失わせない。
    /// </summary>
    public const int ExternalReductionConfirmations = 2;

    /// <summary>
    /// FR-10, #820, IADR-0344 決定6・追記(4) 決定10: <b>S0（ブローカー側逆指値）の行から見た建玉残</b>。
    /// 方向の純額から<b>他の S1 行の残保護数量</b>を差し引く（S1 行が無い構成では差し引く量が 0 で従来と同一）。
    /// <para>
    /// 差し引くのは、S1 の建玉が S0 の「建玉あり」を支えてしまうと、S0 の建玉が消えた後も逆指値が残って
    /// <b>反対建玉</b>を生むためである（#826 項目 3）。発注記録（決済レグ）は<b>一切見ない</b>——
    /// 完了済み S0 行の取消済みレグで持ち分が食われる事故（#820 の 4 巡目監査 BLK-2）を構造的に無くす。
    /// </para>
    /// </summary>
    public static int RemainingPositionFor(
        ProtectiveStopOrder stop,
        IReadOnlyList<BrokerPositionSnapshot> snapshot,
        IEnumerable<ProtectiveStopOrder> activeStops)
    {
        ArgumentNullException.ThrowIfNull(stop);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(activeStops);

        var claimedByOtherMechanisms = activeStops
            .Where(other => other.EntryDecisionId != stop.EntryDecisionId
                && other.State == ProtectiveStopState.Active
                && other.Mechanism != stop.Mechanism
                && other.Symbol == stop.Symbol
                && other.Market == stop.Market
                && other.EntrySide == stop.EntrySide)
            .Sum(other => other.ProtectedQuantity);

        var net = Math.Max(0, DirectionalNet(stop, snapshot) - claimedByOtherMechanisms);

        // 外部要因の割り当て（ReconcileShares）で自分の主張が 0 にされていれば、その分の建玉はもう自分のものではない。
        // 主張が残っていれば従来どおり（未確定＝null の行は旧来の計算のまま＝S1 が無い構成で挙動が変わらない）。
        return stop.RemainingProtected is { } own ? Math.Min(own, net) : net;
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
    /// FR-10, ADR-0040 決定1（S1）, #820 の 4 巡目監査, IADR-0344 追記(4) 決定5-2':
    /// 同じ銘柄・市場・方向の <b>S1 行の残保護数量を確定し、外部要因による減少を一度だけ割り当てて保存する</b>。
    /// 変化した行を保存したうえで、その群の最新の行を返す。
    /// <para>
    /// <b>確定</b>: エントリーの発注記録が終端になっていれば、残保護数量＝その約定数量（未確定の行だけを確定する。
    /// 自分の決済で減った値を約定数量へ戻さない）。記録が無い・未終端の行は<b>未確定のまま</b>で、
    /// 主張もしないし削られもしない（孤立行の扱いは SoftwareStopExecutor の猶予と Critical が受け持つ）。
    /// </para>
    /// <para>
    /// 🔴 <b>割り当ては「帳簿だけの行」から</b>（#820 の 5 巡目監査・IADR-0344 追記(5)）: 超過分は
    /// <b>S1（ブローカーに注文を持たない行）を先に使い切り</b>、<b>S0（実注文を持つ行）は最後</b>に回す。
    /// それぞれの中では <b>作成時刻 → EntryDecisionId</b> の順に古い行から削る。
    /// S0 の行に注文照会が <c>Pending</c> を返していること自体が「その建玉はまだ在る」証拠であり、
    /// 作成時刻だけで順序を決めると<b>ブローカーに実在する生きた逆指値が 0 にされて取り消される</b>（無音・不可逆）。
    /// </para>
    /// <para>
    /// 🔴 <b>S0 行は「全部か 0 か」でしか削らない。</b> S0 の主張はブローカーに実在する逆指値の数量であり、
    /// 帳簿だけを部分的に削っても注文は縮まない——縮まない注文が建玉より大きいまま残ると、発火して<b>反対建玉</b>を作る。
    /// 全部削られた S0 行はガードが<b>逆指値そのものを取り消して</b>完了させるため、帳簿と注文が一致する
    /// （#826 項目 3）。超過が S0 の主張に満たなければその行は飛ばし、次の行（S1 なら部分的に削れる）へ回す。
    /// </para>
    /// <para>
    /// 🔴 <b>削ったことを保存する</b>のがこの設計の要である。次の巡回は保存された値から始まるため、
    /// 同じ減少を二度割り当てないし、持ち分が巡回ごとに揺れることもない。
    /// </para>
    /// <para>
    /// 🔴 <b>削りは「未確定」として持ち、2 巡回連続で観測してから確定する</b>（#820 の 5 巡目監査・IADR-0344 追記(5)）。
    /// 建玉照会が<b>1 巡回だけ過少に返る</b>だけで行が恒久的に失われるのを止める。
    /// <b>数量の減算は観測した巡回で直ちに行う</b>（遅らせると同じ巡回の別の行が古い建玉を再び主張して売り過ぎる）が、
    /// <b>未確定のあいだは行を完了させず、S0 の逆指値も取り消さない</b>。建玉が戻れば<b>削った分を復元する</b>。
    /// 確定（<paramref name="observing"/> の巡回で <see cref="ExternalReductionConfirmations"/> 回目の観測）では
    /// <see cref="SoftwareStopExecuted"/>（<see cref="SoftwareStopOutcome.ProtectionReduced"/>）を<b>1 回だけ</b>
    /// <paramref name="events"/> へ積む——無音の不可逆動作を残さない。
    /// </para>
    /// </summary>
    /// <param name="observing">
    /// この呼び出しを<b>1 回の観測</b>として数えるか。ガードの巡回の先頭（群につき 1 巡回 1 回）だけが <c>true</c> で、
    /// 確定・復元を行う。決済経路（<c>SoftwareStopExecutor</c>）からの呼び出しは数量の割り当てだけを行う。
    /// </param>
    /// <param name="events">確定したときに発行すべきイベントの受け皿（発行は呼び出し側）。</param>
    public static IReadOnlyList<ProtectiveStopOrder> ReconcileShares(
        string symbol,
        Market market,
        TradeSide entrySide,
        IReadOnlyList<BrokerPositionSnapshot> snapshot,
        IEnumerable<ProtectiveStopOrder> activeStops,
        IProtectiveStopOrderStore stops,
        IExecutedOrderStore store,
        DateTimeOffset now,
        bool observing = false,
        ICollection<object>? events = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(activeStops);
        ArgumentNullException.ThrowIfNull(stops);
        ArgumentNullException.ThrowIfNull(store);

        var group = ConfirmEntryFills(activeStops, stops, store, now)
            .Where(s => s.State == ProtectiveStopState.Active
                && s.Symbol == symbol && s.Market == market && s.EntrySide == entrySide)
            .ToList();

        // 🔴 **S1 の主張が 1 株も無い群には手を触れない。** 割り当ての目的は S1 の持ち分を確定させることであり、
        // S0 だけの群（実弾・S0 のみの構成）では従来の判定がそのまま働く——ここで S0 の主張を削ると
        // 「同一銘柄に複数の S0 が併存し純額がそれを賄えない」既存の近似の挙動が変わってしまう（IADR-0344 追記(4)）。
        // ただし**未確定の削りを抱えた行がある群は例外**である（確定・復元をここでしか解けないため。追記(5)）。
        if (group.Where(s => s.IsSoftwareStop).Sum(s => s.ProtectedQuantity) <= 0
            && group.All(s => !s.HasUnconfirmedExternalReduction))
        {
            return group;
        }

        var excess = group.Sum(s => s.ProtectedQuantity) - DirectionalNet(symbol, market, entrySide, snapshot);

        if (excess > 0)
            Reduce(group, excess, stops, now);
        else if (observing && excess < 0)
            Restore(group, -excess, stops, now);

        return observing ? Confirm(group, stops, events, now) : group;
    }

    // 超過分を「帳簿だけの行（S1）→ 実注文を持つ行（S0）」の順に削り、削った分を**未確定**として積む。
    private static void Reduce(
        List<ProtectiveStopOrder> group, int excess, IProtectiveStopOrderStore stops, DateTimeOffset now)
    {
        foreach (var row in group
            .Where(s => s.ProtectedQuantity > 0)
            .OrderBy(s => s.IsSoftwareStop ? 0 : 1)
            .ThenBy(s => s.CreatedAt)
            .ThenBy(s => s.EntryDecisionId)
            .ToList())
        {
            if (excess <= 0)
                break;

            // S0 は部分的に削れない（ブローカーの注文を縮められないため）。超過が主張に届かなければ飛ばす。
            if (!row.IsSoftwareStop && row.ProtectedQuantity > excess)
                continue;

            var take = Math.Min(row.ProtectedQuantity, excess);
            var reduced = row with
            {
                RemainingProtected = row.ProtectedQuantity - take,
                PendingExternalReduction = row.PendingExternalReduction + take,
                UpdatedAt = now,
            };
            Replace(group, reduced, stops);
            excess -= take;
        }
    }

    // 建玉が戻った（照会が 1 巡回だけ過少に見えていた）。**未確定の削りだけ**を新しい行から返す。
    // 確定済みの削りは戻さない——「一度だけ確定的に割り当てる」性質（持ち分が巡回ごとに揺れない）を壊さないため。
    private static void Restore(
        List<ProtectiveStopOrder> group, int surplus, IProtectiveStopOrderStore stops, DateTimeOffset now)
    {
        foreach (var row in group
            .Where(s => s.HasUnconfirmedExternalReduction)
            .OrderBy(s => s.IsSoftwareStop ? 1 : 0)
            .ThenByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.EntryDecisionId)
            .ToList())
        {
            if (surplus <= 0)
                break;

            // S0 は「全部か 0 か」（削るときと同じ理由。帳簿だけ部分的に戻しても注文は変わらない）。
            var give = row.IsSoftwareStop ? Math.Min(row.PendingExternalReduction, surplus) : row.PendingExternalReduction;
            if (give <= 0 || give > surplus)
                continue;

            var pending = row.PendingExternalReduction - give;
            var restored = row with
            {
                RemainingProtected = (row.RemainingProtected ?? 0) + give,
                PendingExternalReduction = pending,
                ExternalReductionObservations = pending > 0 ? row.ExternalReductionObservations : 0,
                UpdatedAt = now,
            };
            Replace(group, restored, stops);
            surplus -= give;
        }
    }

    // 観測を 1 回数え、規定回数に達した未確定の削りを確定して**1 回だけ**通知する。
    private static IReadOnlyList<ProtectiveStopOrder> Confirm(
        List<ProtectiveStopOrder> group, IProtectiveStopOrderStore stops, ICollection<object>? events, DateTimeOffset now)
    {
        foreach (var row in group.Where(s => s.HasUnconfirmedExternalReduction).ToList())
        {
            var observations = row.ExternalReductionObservations + 1;
            if (observations < ExternalReductionConfirmations)
            {
                Replace(group, row with { ExternalReductionObservations = observations, UpdatedAt = now }, stops);
                continue;
            }

            Replace(
                group,
                row with { PendingExternalReduction = 0, ExternalReductionObservations = 0, UpdatedAt = now },
                stops);

            // 🔴 無音にしない。外部要因で保護対象を減らしたことを必ず 1 回残す（S0 の行を 0 にして
            // 逆指値を取り消す場合は、この記録が唯一の痕跡になる）。
            events?.Add(new SoftwareStopExecuted(
                row.EntryDecisionId, row.Symbol, row.Market, SoftwareStopOutcome.ProtectionReduced,
                row.PendingExternalReduction, row.TriggerPrice, row.TriggeredPrice ?? row.TriggerPrice, row.Attempt,
                CloseDecisionId: null, CloseOrderId: null, CloseIntent: null, now));
        }

        return group;
    }

    private static void Replace(
        List<ProtectiveStopOrder> group, ProtectiveStopOrder updated, IProtectiveStopOrderStore stops)
    {
        stops.Save(updated);
        group[group.FindIndex(s => s.EntryDecisionId == updated.EntryDecisionId)] = updated;
    }

    /// <summary>
    /// #820 の 4 巡目監査, IADR-0344 追記(4): <b>確定</b>だけを行う（建玉の照会が要らない）。
    /// エントリーの発注記録が終端になった S1 行の残保護数量を、その約定数量で確定して保存する。
    /// 記録が無い・まだ終端でない行は<b>未確定のまま</b>（主張 0）で、割り当ての対象にもしない。
    /// <para>
    /// ガードは巡回の<b>最初</b>にこれを通す——S0 行の建玉残は S1 行の残保護数量を差し引いて判定するため、
    /// 確定していない S1 行があると S0 が「建玉が自分のもの」と誤認する（#826 項目 3 が 1 巡回ぶん効かなくなる）。
    /// </para>
    /// </summary>
    public static IReadOnlyList<ProtectiveStopOrder> ConfirmEntryFills(
        IEnumerable<ProtectiveStopOrder> activeStops,
        IProtectiveStopOrderStore stops,
        IExecutedOrderStore store,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(activeStops);
        ArgumentNullException.ThrowIfNull(stops);
        ArgumentNullException.ThrowIfNull(store);

        var rows = activeStops.ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!row.IsSoftwareStop || row.IsEntryFillConfirmed || row.State != ProtectiveStopState.Active)
                continue;

            var entry = store.FindByDecisionId(row.EntryDecisionId);
            if (entry is null || !OrderStatusLifecycle.IsTerminal(entry.Status))
                continue; // 記録が無い・これから約定し得る → 未確定のまま（主張 0）。

            var confirmed = row with { RemainingProtected = Math.Max(0, entry.FilledQuantity), UpdatedAt = now };
            stops.Save(confirmed);
            rows[i] = confirmed;
        }

        return rows;
    }
}
