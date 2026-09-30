using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-04, FR-02, ADR-0048 決定 2・3, #1118, IADR-0467 決定 3: 前営業日までの確定した日足を、銘柄 × 取引日で 1 回だけ取って覚える（singleton）。
//
// 🔴 **窓の両端で守る**（作業仕様書の窓の表）:
//   - 前の端: キャッシュは「取得した時点の取引日（市場ローカルの日付）」で区切る。日付が変わったら取り直す（前日の取得を翌日に使わない。
//     分割が寄りで効いた日に、古い取得と新しい取得の足を混ぜない）。
//   - 後の端: 要求の期間の終わりは取引日の前日にし、応答のうち取引日以降の足（未確定の当日足）を必ず捨てる。
//   - 最後の足が期待する前営業日かの突き合わせは計算側（DailyVolumeContext）が行う。
// 🔴 **足し継がない**。取引日ごとに期間全体を取り直す（前復権の基準は取得ごとに揃う。分割をまたいで混ぜない）。
// 🔴 **失敗は <see cref="FailureRetryInterval"/> の間覚えて撃ち直さない**（判断のサイクルごとに叩かない）。成功は取引日の終わりまで。
// 🔴 **米国株だけ**を取る（日足 K 線の履歴源は米国株。ADR-0023 決定 5）。日本株は要求せず null。
// 取得は 1 本ずつ（直列化）。1 取引日の取得回数は銘柄の数（＋失敗の撃ち直し）で抑えられる。
public sealed class CachedDailyBarsProvider(
    IDailyBarsSource source,
    TimeProvider timeProvider,
    ILogger<CachedDailyBarsProvider> logger)
    : IDailyBarsProvider, IDisposable
{
    /// <summary>
    /// 要求する期間の長さ（前営業日から遡る暦日）。20 本（出来高の平均）と 15 本（ATR(14)。#1122）に、休場の多い月でも足りる幅
    /// （#1117 の実測で 2 か月＝46 本）。
    /// </summary>
    public const int LookbackCalendarDays = 45;

    /// <summary>取得に失敗した銘柄を撃ち直すまでの間隔。</summary>
    public static readonly TimeSpan FailureRetryInterval = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(string Symbol, Market Market), Entry> _cache = new();

    public bool IsEnabled => true;

    public async Task<ConfirmedDailyBars?> GetConfirmedBarsAsync(
        string symbol, Market market, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (market != Market.UnitedStates)
            return null;

        var now = timeProvider.GetUtcNow();
        var tradingDay = MarketTradingDays.TradingDateOf(market, now);
        var key = (symbol.ToUpperInvariant(), market);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(key, out var cached) && cached.TradingDay == tradingDay)
            {
                if (cached.Bars is not null)
                    return cached.Bars;
                if (now < cached.RetryAt)
                    return null;
            }

            var expectedPrevious = MarketTradingDays.PreviousTradingDay(market, tradingDay);
            var from = expectedPrevious.AddDays(-LookbackCalendarDays);
            var to = tradingDay.AddDays(-1);

            IReadOnlyList<DailyBar>? fetched;
            try
            {
                fetched = await source.FetchAsync(symbol, market, from, to, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "日足の取得で例外。出来高は未提供として扱います: {Symbol}", symbol);
                fetched = null;
            }

            if (fetched is null)
            {
                _cache[key] = new Entry(tradingDay, null, now + FailureRetryInterval);
                logger.LogInformation(
                    "日足を取得できない。{Minutes} 分は撃ち直さない（出来高は未提供）: {Symbol} tradingDay={TradingDay}",
                    FailureRetryInterval.TotalMinutes, symbol, tradingDay);
                return null;
            }

            // 🔴 取引日以降の足（未確定の当日足）を捨てる。重複した日付は先の 1 本だけを残し、昇順に並べる。
            var confirmed = fetched
                .Where(b => b.Date < tradingDay)
                .GroupBy(b => b.Date)
                .Select(g => g.First())
                .OrderBy(b => b.Date)
                .ToList();
            var result = new ConfirmedDailyBars(tradingDay, expectedPrevious, confirmed);
            _cache[key] = new Entry(tradingDay, result, default);
            logger.LogInformation(
                "日足を取得（前営業日まで・前復権）: {Symbol} tradingDay={TradingDay} bars={Bars} last={Last} droppedUnconfirmed={Dropped}",
                symbol, tradingDay, confirmed.Count, confirmed.Count > 0 ? confirmed[^1].Date : null,
                fetched.Count - confirmed.Count);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private sealed record Entry(DateOnly TradingDay, ConfirmedDailyBars? Bars, DateTimeOffset RetryAt);
}
