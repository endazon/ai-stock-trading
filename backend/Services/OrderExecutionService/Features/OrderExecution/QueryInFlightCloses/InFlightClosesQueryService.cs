using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;

namespace OrderExecutionService.Features.OrderExecution.QueryInFlightCloses;

// 🔴 FR-04, FR-10, #1338, IADR-0535 決定2: 「今この方向の決済を送れば、決済のゲートが InFlightCloseCoversPosition で見送るか」を答える（scoped）。
// 取引判断が LLM の前に `GET /order-execution/in-flight-closes` で呼ぶ。**判定はゲートと同じ関数（InFlightCloses）**であり、ここは順序と倒し方だけを持つ。
//
//   1. 建玉照会の能力・保護記録ストアが無い（内蔵 paper）→ Unknown（ゲートもこの構成では処理中の決済を引かない）。
//   2. 記録に処理中の決済の候補が無い → NotCovered（**ブローカーへは照会しない**。処理中の決済が無い通常時に OpenD を叩かない）。
//   3. 候補があれば、ブローカーの建玉を照会し（不明 → Unknown）、🔴 **照会の後に候補を読み直して**（IADR-0461 決定4。照会の最中に保存された
//      決済を取りこぼさない）、1 件ごとに生死を確かめて数える。決済方向の建玉が 0 なら NotCovered（ゲートは別の理由で扱う）。
//
// どの失敗も Unknown か NotCovered（受け手は LLM を呼ぶ）へ倒す。Covered へ倒れる経路は「数えた処理中の決済が建玉を覆う」だけである。
public sealed class InFlightClosesQueryService(
    IExecutedOrderStore store,
    IBrokerAdapter broker,
    ILogger<InFlightClosesQueryService> logger,
    IProtectiveStopOrderStore? protectiveStops = null,
    IBrokerPositionSource? brokerPositions = null)
{
    public async Task<InFlightClosesView> QueryAsync(
        string symbol, Market market, TradeSide closeSide, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        if (brokerPositions is null || protectiveStops is null)
            return Unknown(symbol, market, closeSide, InFlightClosesUnknownReasons.BrokerNotSupported);

        if (FindCandidatesSafe(symbol, market, closeSide) is not { } before)
            return Unknown(symbol, market, closeSide, InFlightClosesUnknownReasons.RecordsUnavailable);
        if (before.Count == 0)
            return View(symbol, market, closeSide, InFlightClosesStatus.NotCovered, null, InFlightCloseCount.None);

        IReadOnlyList<BrokerPositionSnapshot>? snapshot;
        try
        {
            snapshot = (await PositionQueries.QueryAsync(brokerPositions, cancellationToken).ConfigureAwait(false)).Positions;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "処理中の決済の照会で、ブローカーの建玉照会が例外で失敗しました（不明として答えます）: {Symbol}", symbol);
            snapshot = null;
        }

        if (snapshot is null)
            return Unknown(symbol, market, closeSide, InFlightClosesUnknownReasons.PositionsIndeterminate);

        // 決済方向の建玉はゲートと同じ純関数で数える（方向ごと。ネットで数えない）。数量は判定に使わないので 1 を置く。
        var probe = new OrderIntent(symbol, market, closeSide, ProductType.Cash, broker.Provider, 1, 0m, PositionEffect.Close);
        var closable = BrokerHeldPositionGate.Evaluate(probe, snapshot).ClosableQuantity;

        if (FindCandidatesSafe(symbol, market, closeSide) is not { } after)
            return Unknown(symbol, market, closeSide, InFlightClosesUnknownReasons.RecordsUnavailable);

        var inFlight = await InFlightCloses.CountLiveAsync(after, broker, cancellationToken).ConfigureAwait(false);
        var status = closable > 0 && InFlightCloses.Covers(closable, inFlight.Quantity)
            ? InFlightClosesStatus.Covered
            : InFlightClosesStatus.NotCovered;

        logger.LogInformation(
            "処理中の決済の照会: {Symbol}/{Market} 決済の方向={CloseSide} 結果={Status} 決済方向の建玉={Closable} 処理中={InFlight} 株・{Count} 件 注文={OrderIds}",
            symbol, market, closeSide, status, closable, inFlight.Quantity, inFlight.OrderIds.Count, string.Join(",", inFlight.OrderIds));
        return View(symbol, market, closeSide, status, closable, inFlight);
    }

    private IReadOnlyList<ExecutionRecord>? FindCandidatesSafe(string symbol, Market market, TradeSide closeSide)
    {
        try
        {
            return InFlightCloses.FindCandidates(store, protectiveStops!, symbol, market, closeSide);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "処理中の決済の記録を読めませんでした（不明として答えます）: {Symbol}", symbol);
            return null;
        }
    }

    private static InFlightClosesView View(
        string symbol, Market market, TradeSide closeSide, InFlightClosesStatus status, int? closable, InFlightCloseCount inFlight) =>
        new(symbol, market, closeSide, status, null, closable, inFlight.Quantity, inFlight.OrderIds, inFlight.DecisionIds);

    private static InFlightClosesView Unknown(string symbol, Market market, TradeSide closeSide, string reason) =>
        new(symbol, market, closeSide, InFlightClosesStatus.Unknown, reason, null, 0, [], []);
}
