using Microsoft.Extensions.Logging;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution;

/// <summary>#1222, IADR-0515 / #1452, IADR-0578: 利用者の手仕舞い・維持率割れの自動縮小を差し引いた結果の種類。</summary>
public enum OwnerCloseNettingKind
{
    /// <summary>生きている利用者・自動縮小の決済が無い・読めない（従来どおり送る）。</summary>
    NotApplicable,

    /// <summary>差し引いた（Sendable が送れる数量）。</summary>
    Applied,

    /// <summary>差し引くための建玉照会が不明（送らない）。</summary>
    PositionsUnknown,
}

/// <summary>#1222, IADR-0515 / #1452, IADR-0578: 利用者の手仕舞い・維持率割れの自動縮小を差し引いた結果。</summary>
public readonly record struct OwnerCloseNettingResult(
    OwnerCloseNettingKind Kind, int Sendable, int Closable, int InFlight, IReadOnlyList<Guid> OwnerCloseDecisionIds)
{
    public static readonly OwnerCloseNettingResult NotApplicable = new(OwnerCloseNettingKind.NotApplicable, 0, 0, 0, []);
}

// 🔴 FR-10, UC-06, ADR-0050 決定1, #1222, IADR-0515 決定4 / #1452, IADR-0578: 取消の段（DecisionCloseYield）が取り消さなかった
// 利用者の手仕舞い・維持率割れの自動縮小が生きているとき、**送れる数量＝決済方向の建玉 − 処理中の決済の残りの合計**を返す
// （判断側 IADR-0461 の CountInFlightClosesAsync と同じ物差し）。損切りの成行を送る 2 つの経路——S1（SoftwareStopExecutor）と
// 保護逆指値ガードの成行手仕舞い（ProtectiveStopGuard）——が、**同じ決済の直列化（IADR-0575）の内側で、取消の段の後・成行の予約の前に**呼ぶ
// （IADR-0578 で S1 から移設した。挙動は IADR-0515 決定4 のまま）。
//   - 処理中の決済: 同じ銘柄・市場・決済の方向の非終端の Close の記録のうち、**ブローカー側の保護逆指値レグ（S0 / S3）を除いた**もの
//     （S1 の決済〔自分の前の試行・他の記録〕・保護喪失の成行手仕舞い・利用者・自動縮小）。保護レグは SIMULATE では売れる数量を押さえない
//     （IADR-0461 と同じ。押さえるかは ADR-0050 決定 3 の実弾の確認項目）。残り＝記録の数量 − 約定（記録と照会の大きい方）。
//   - 🔴 **証券会社が生きていると答えたものだけを数える**。確かめられない（null・例外）・終端は数えない＝差し引かずに送る側（損切りを止めない）。
//   - 生きている利用者・自動縮小の決済が 1 本も無ければ NotApplicable（建玉照会を増やさない・従来の挙動）。
//   - 🔴 **猶予（NettedCloseGrace）はここでは見ない。** 取消の段が取り消さなかった生きている利用者・自動縮小の決済はすべて引く。
//     取消の段と別に時計を読んで猶予を判定すると、1 回の試行の中で猶予をまたいだ決済が「取消の段では猶予の内・ここでは猶予の外」になり、
//     取り消しも差し引きもされずに全量を送る（独立監査 R1。押さえない証券会社では二重に売る）。猶予を過ぎて取り消した決済は終端なので数えない。
//   - 照会の順は「記録 → 注文照会 → 建玉照会（新しく）」。間に約定が進むと建玉は約定の後・残りは約定の前になり、**差し引き過ぎる**
//     （少なく売り、残りは次の巡回で売る）。逆の順は差し引き不足（二重に売り得る）になるので採らない（仕様書 20260930_1121 の窓の表）。
//   - 記録の読み出しの失敗は NotApplicable（差し引かずに送る。是正前と同じ側）。建玉照会の不明（null）は PositionsUnknown。
//   - 建玉照会は呼び出し側が渡す（照会の成否の報告〔IADR-0462〕の扱いが経路ごとに違うため。S1 は報告し、ガードは巡回の先頭の 1 回に留める）。
//   - 取り消さない（利用者の意思を尊重する IADR-0515 決定3 の規則を保つ）。記録は書かない。
public static class OwnerCloseNetting
{
    // 保護記録を引く上限。保有建玉数上限（既定 3）に対して十分大きい。
    private const int ScanLimit = 500;

    /// <summary>
    /// 処理中の利用者の手仕舞い・維持率割れの自動縮小（とほかの処理中の決済）を差し引いた、送れる数量を返す。
    /// <paramref name="queryPositions"/> は建玉を新しく照会する（null＝不明）。<paramref name="actor"/> はログの主語。
    /// </summary>
    public static async Task<OwnerCloseNettingResult> NetAsync(
        ProtectiveStopOrder stop,
        IBrokerAdapter broker,
        IProtectiveStopOrderStore stops,
        IExecutedOrderStore store,
        Func<CancellationToken, Task<IReadOnlyList<BrokerPositionSnapshot>?>> queryPositions,
        ILogger logger,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stop);
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(stops);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(queryPositions);
        ArgumentNullException.ThrowIfNull(logger);

        List<ExecutionRecord> candidates;
        try
        {
            var pending = store.FindPendingCloses(stop.Symbol, stop.Market, stop.CloseSide);
            if (!pending.Any(DecisionCloseYield.IsOwnerOrigin))
                return OwnerCloseNettingResult.NotApplicable;

            var legDecisionIds = new HashSet<Guid>();
            var legOrderIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in stops.FindRecentFor(stop.Symbol, stop.Market, stop.EntrySide, ScanLimit).Append(stop))
            {
                if (row.IsSoftwareStop)
                    continue; // S1 はブローカーへ逆指値を出さない（StopDecisionId は注文に対応しない）。
                legDecisionIds.Add(row.StopDecisionId);
                if (!string.IsNullOrEmpty(row.StopOrderId))
                    legOrderIds.Add(row.StopOrderId);
                for (var attempt = 1; attempt <= Math.Max(row.Attempt, 0) + 1; attempt++)
                    legDecisionIds.Add(ProtectiveStopIds.StopDecisionId(row.EntryDecisionId, attempt));
            }

            candidates = pending
                .Where(r => !legDecisionIds.Contains(r.DecisionId) && !legOrderIds.Contains(r.OrderId))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "処理中の利用者の手仕舞い・維持率割れの自動縮小を読めませんでした（差し引かずに{Actor}を送ります。証券会社に拒否され得ます）。"
                    + "EntryDecisionId={EntryDecisionId} 銘柄={Symbol}",
                actor, stop.EntryDecisionId, stop.Symbol);
            return OwnerCloseNettingResult.NotApplicable;
        }

        var inFlight = 0;
        var ownerCloses = new List<Guid>();
        foreach (var record in candidates)
        {
            var live = await DecisionCloseYield.TryGetOrderAsync(broker, logger, record.OrderId, cancellationToken)
                .ConfigureAwait(false);
            if (live is null || OrderStatusLifecycle.IsTerminal(live.Status))
                continue;

            var remaining = record.Quantity - Math.Max(record.FilledQuantity, live.FilledQuantity);
            if (remaining <= 0)
                continue;

            inFlight += remaining;
            if (DecisionCloseYield.IsOwnerOrigin(record))
                ownerCloses.Add(record.DecisionId);
        }

        if (ownerCloses.Count == 0)
            return OwnerCloseNettingResult.NotApplicable;

        // 建玉は注文照会の**後**に照会し直す（上の順の理由）。渡されたスナップショット・巡回の先頭の照会は使わない。
        var positions = await queryPositions(cancellationToken).ConfigureAwait(false);
        if (positions is null)
            return new OwnerCloseNettingResult(OwnerCloseNettingKind.PositionsUnknown, 0, 0, 0, ownerCloses);

        // 決済方向の建玉を方向ごとに数える（判断側の BrokerHeldPositionGate と同じ数え方。ネットは使わない）。
        var closable = positions
            .Where(p => p.Symbol == stop.Symbol && p.Market == stop.Market
                && (stop.CloseSide == TradeSide.Sell ? p.Quantity > 0 : p.Quantity < 0))
            .Sum(p => Math.Abs(p.Quantity));
        return new OwnerCloseNettingResult(
            OwnerCloseNettingKind.Applied, Math.Max(0, closable - inFlight), closable, inFlight, ownerCloses);
    }
}
