using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution.RetrofitStopWidthFloor;

// 🔴 FR-10, FR-11, ADR-0049, #1136（オーナー裁定 2026-10-01）, IADR-0472: 損切り幅の下限を、下限の導入（#1120・IADR-0465）より前に建てた
// **Active・未到達の S1 の保護記録**へ遡及する。常駐ガード（ProtectiveStopGuard）が巡回の先頭で毎回呼ぶ（起動直後の初回を含む）。
// 規則は StopWidthFloorRetrofitPolicy（取得単価 × 2%・広げる向きだけ）。冪等であり、2 回目以降は書かず事実も出さない。
//
// 触らないもの（裁定）:
//   - S0 / S3（ブローカー側の注文。丸めた発火価格を持つ）・完了・AwaitingEntry。
//   - 🔴 到達済み（TriggeredAt あり。再武装した行を含む）—— 決済を続ける行のラインを動かさない。
//   - 🔴 決済が処理中の群（同じ銘柄・市場・方向）—— 非終端の Close の記録から Active な S0 / S3 の保護レグを除き、
//     ブローカーが生きていると答えたものが 1 件でもあれば、その群はこの巡回では当てない（IADR-0461 決定1・4 と同じ見分け方）。
//     確かめられない（照会 null・例外）ものは数えない（恒久に照会できない古い記録で遡及が永遠に止まらないように。広げる向きなので損切りを早めない）。
//   - 取得単価が分からない行（エントリーの発注記録が無い・約定 0・平均価格 0 以下）。次の巡回で当て直す。
//
// 🔴 競合（IADR-0396）: 書き込みは stops.Update（保存先の最新の行で対象の条件と規則を判定し直し、版が一致したときだけ書く）。
// 到達の購読が並行に武装しても、到達の記録を巻き戻さない（窓 A の P2）。逆向き（候補を読んだ後に広げた）は
// SoftwareStopExecutor.OnTriggeredAsync が最新の行のラインで到達を判定し直して塞ぐ（窓 A の P1）。
public sealed class SoftwareStopFloorRetrofitter(
    IProtectiveStopOrderStore stops,
    IExecutedOrderStore store,
    IBrokerAdapter broker,
    IClock clock,
    ILogger<SoftwareStopFloorRetrofitter>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<SoftwareStopFloorRetrofitter>.Instance;

    /// <summary>
    /// <paramref name="active"/>（巡回で読んだ Active な記録の写し）のうち対象の S1 の行へ下限を遡及し、広げた行ごとの事実を返す（発行は呼び出し側）。
    /// 写しは候補の絞り込みにだけ使い、判定と書き込みは保存先の最新の行で行う。
    /// </summary>
    public async Task<IReadOnlyList<SoftwareStopLineWidened>> ApplyAsync(
        IReadOnlyList<ProtectiveStopOrder> active, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(active);

        var events = new List<SoftwareStopLineWidened>();
        foreach (var group in active
            .Where(IsTarget)
            .GroupBy(s => (s.Symbol, s.Market, s.EntrySide)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 前の端（写し）で広げる必要のある行だけに絞る。無ければブローカーへ何も問い合わせない。
            var candidates = group
                .Select(s => (Stop: s, EntryPrice: EntryPriceOf(s)))
                .Where(c => c.EntryPrice is { } price
                    && StopWidthFloorRetrofitPolicy.Widen(c.Stop.EntrySide, c.Stop.TriggerPrice, price) is not null)
                .ToList();
            if (candidates.Count == 0)
                continue;

            var (symbol, market, entrySide) = group.Key;
            if (await HasLiveCloseInFlightAsync(symbol, market, entrySide, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "ソフトウェア逆指値の損切りラインの遡及を見送ります（同じ建玉の決済が処理中です。次の巡回で当て直します）。"
                        + "銘柄={Symbol} 市場={Market} 建玉方向={EntrySide} 対象 {Count} 件",
                    symbol, market, entrySide, candidates.Count);
                continue;
            }

            foreach (var (stop, entryPrice) in candidates)
            {
                if (Widen(stop, entryPrice!.Value) is { } widened)
                    events.Add(widened);
            }
        }

        return events;
    }

    // 対象: Active・未到達の S1。
    private static bool IsTarget(ProtectiveStopOrder stop) =>
        stop.IsSoftwareStop && stop.State == ProtectiveStopState.Active && stop.TriggeredAt is null;

    // 取得単価＝エントリーの発注記録の平均約定価格（約定 1 株以上・正の値）。分からなければ null（当てない）。
    private decimal? EntryPriceOf(ProtectiveStopOrder stop)
    {
        var entry = store.FindByDecisionId(stop.EntryDecisionId);
        return entry is { PositionEffect: PositionEffect.Open, FilledQuantity: > 0, AveragePrice: > 0m }
            ? entry.AveragePrice
            : null;
    }

    private SoftwareStopLineWidened? Widen(ProtectiveStopOrder stop, decimal entryPrice)
    {
        var now = clock.UtcNow;
        decimal previous = 0m;
        ProtectiveStopOrder? saved;
        try
        {
            // 🔴 後の端: 保存先の最新の行で、対象か・広げる向きかを判定し直す（並行に武装・完了していれば書かない）。
            saved = stops.Update(stop.EntryDecisionId, fresh =>
            {
                if (!IsTarget(fresh))
                    return null;
                if (StopWidthFloorRetrofitPolicy.Widen(fresh.EntrySide, fresh.TriggerPrice, entryPrice) is not { } line)
                    return null;

                previous = fresh.TriggerPrice;
                return fresh with { TriggerPrice = line, UpdatedAt = now };
            });
        }
        catch (ProtectiveStopConcurrencyException ex)
        {
            _logger.LogError(ex,
                "🔴 ソフトウェア逆指値の損切りラインの遡及が並行更新と衝突し続けました（書いていません。次の巡回で当て直します）。"
                    + "EntryDecisionId={EntryDecisionId} 銘柄={Symbol}",
                stop.EntryDecisionId, stop.Symbol);
            return null;
        }

        if (saved is null)
            return null;

        var floor = StopWidthFloorRetrofitPolicy.FloorPerShare(entryPrice);
        _logger.LogInformation(
            "ソフトウェア逆指値の損切りラインを下限まで広げました（遡及）: 銘柄={Symbol} 建玉方向={EntrySide} "
                + "旧ライン={PreviousStopLoss} → 新ライン={StopLoss}（取得単価 {EntryPrice}・下限 {Floor} {FloorSource}）。"
                + "EntryDecisionId={EntryDecisionId}",
            saved.Symbol, saved.EntrySide, previous, saved.TriggerPrice, entryPrice, floor,
            StopWidthFloorSource.Fallback2Pct, saved.EntryDecisionId);

        return new SoftwareStopLineWidened(
            saved.EntryDecisionId, saved.Symbol, saved.Market, saved.EntrySide, entryPrice, previous, saved.TriggerPrice,
            floor, StopWidthFloorSource.Fallback2Pct, now);
    }

    // 🔴 IADR-0461 決定1・4 と同じ見分け方: 非終端の Close の記録（同じ銘柄・市場・決済の方向）から、Active な S0 / S3 の保護レグ
    // （StopOrderId / StopDecisionId が一致）を除き、ブローカーが生きている（非終端）と答えたものが 1 件でもあれば処理中。
    private async Task<bool> HasLiveCloseInFlightAsync(
        string symbol, Market market, TradeSide entrySide, CancellationToken cancellationToken)
    {
        var closeSide = entrySide == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;
        var protectiveLegs = stops.FindActiveFor(symbol, market, entrySide)
            .Where(s => s.State == ProtectiveStopState.Active && !s.IsSoftwareStop)
            .ToList();
        var legOrderIds = protectiveLegs
            .Select(s => s.StopOrderId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet(StringComparer.Ordinal);
        var legDecisionIds = protectiveLegs.Select(s => s.StopDecisionId).ToHashSet();

        foreach (var record in store.FindPendingCloses(symbol, market, closeSide))
        {
            if (legOrderIds.Contains(record.OrderId) || legDecisionIds.Contains(record.DecisionId))
                continue;

            BrokerOrder? live;
            try
            {
                live = await broker.GetOrderAsync(record.OrderId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "処理中の決済の状態を照会できません（数えずに遡及します）。OrderId={OrderId} 銘柄={Symbol}", record.OrderId, symbol);
                continue;
            }

            if (live is not null && !OrderStatusLifecycle.IsTerminal(live.Status))
                return true;
        }

        return false;
    }
}
