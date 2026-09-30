using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using OrderExecutionService.Common.Abstractions;

namespace OrderExecutionService.Features.OrderExecution.QueryDailyBars;

// FR-04, FR-15, ADR-0048 決定 2・3, ADR-0023 決定 5, #1118, IADR-0467 決定 2・5: 日足の照会（singleton）。
// 取引判断が `GET /order-execution/daily-bars` で呼ぶ（判断へ渡す出来高。#1122 の ATR(14) も同じ日足を使う）。
//
// 🔴 **キャッシュは持たない。** 前営業日までの確定足は 1 日 1 回で足り、その「日」は判断側が米国東部の取引日で決める
// （判断側の CachedDailyBarsProvider）。ここでキャッシュを重ねると、日付の境界の判定が 2 か所になる。
// 🔴 **要求は 1 本ずつ流す**（直列化）。相場の接続は 1 本であり、応答の相関は接続が持つが、要求の頻度を数えやすくする。
// 🔴 **自制の予算**: 実際に撃った回数（成否を問わない）を <see cref="BudgetWindow"/> の窓で数え、<see cref="BudgetPerWindow"/> 回に
//   達したら撃たずに rate-limited を返す（BacktestService の自制レート 30 回/分より内側）。
// 🔴 **取得枠の消費を追えるようにする**: 撃つたびに要求の件数（outcome）と、取得の直後に照会した枠（使用数・残り）を計器とログに出す。
// 米国株以外・発注先が OpenD を持たない（内蔵 paper）ときは撃たない。
public sealed class DailyBarsQueryService(
    IClock clock,
    BusinessMetrics metrics,
    ILogger<DailyBarsQueryService> logger,
    IDailyKLineSource? source)
{
    public static readonly TimeSpan BudgetWindow = TimeSpan.FromSeconds(60);
    public const int BudgetPerWindow = 25;

    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Queue<DateTimeOffset> _attempts = new();

    public async Task<DailyBarsView> QueryAsync(
        string symbol, Market market, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        if (market != Market.UnitedStates)
            return Unavailable(symbol, market, from, to, DailyBarsUnavailableReasons.MarketNotSupported);
        if (source is null)
            return Unavailable(symbol, market, from, to, DailyBarsUnavailableReasons.BrokerNotSupported);

        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = clock.UtcNow;
            while (_attempts.Count > 0 && _attempts.Peek() <= now - BudgetWindow)
                _attempts.Dequeue();
            if (_attempts.Count >= BudgetPerWindow)
            {
                logger.LogWarning(
                    "日足 K 線の自制の予算（{Window} 秒に {Budget} 回）を使い切ったため撃たない: {Symbol}",
                    BudgetWindow.TotalSeconds, BudgetPerWindow, symbol);
                return Unavailable(symbol, market, from, to, DailyBarsUnavailableReasons.RateLimited);
            }

            _attempts.Enqueue(now);
            DailyKLineFetch fetch;
            try
            {
                fetch = await source.FetchForwardAdjustedAsync(symbol, from, to, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                metrics.RecordKLineDailyRequest(BusinessMetrics.KLineRequestFailed);
                logger.LogWarning(ex, "日足 K 線の取得に失敗（前復権）: {Symbol} {From}〜{To}", symbol, from, to);
                return Unavailable(symbol, market, from, to, DailyBarsUnavailableReasons.QueryFailed);
            }

            metrics.RecordKLineDailyRequest(
                fetch.Succeeded ? BusinessMetrics.KLineRequestSucceeded : BusinessMetrics.KLineRequestNonSuccess);
            metrics.RecordKLineQuota(fetch.QuotaUsed, fetch.QuotaRemaining);
            logger.LogInformation(
                "日足 K 線を取得（前復権）: {Symbol} {From}〜{To} succeeded={Succeeded} bars={Bars} reason={Reason} "
                    + "quotaUsed={QuotaUsed} quotaRemaining={QuotaRemaining}",
                symbol, from, to, fetch.Succeeded, fetch.Bars.Count, fetch.FailureReason ?? "-",
                (object?)fetch.QuotaUsed ?? "不明", (object?)fetch.QuotaRemaining ?? "不明");

            if (!fetch.Succeeded)
                return Unavailable(symbol, market, from, to, DailyBarsUnavailableReasons.QueryFailed);

            return new DailyBarsView(
                symbol, market, DailyBarsStatus.Available, null, from, to,
                [.. fetch.Bars.OrderBy(b => b.Date)]);
        }
        finally
        {
            _serial.Release();
        }
    }

    private static DailyBarsView Unavailable(string symbol, Market market, DateOnly from, DateOnly to, string reason) =>
        new(symbol, market, DailyBarsStatus.Unavailable, reason, from, to, []);
}
