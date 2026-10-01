using System.Collections.Concurrent;
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
// 🔴 **失敗は <see cref="FailureRetryInterval"/> の間覚えて撃ち直さない**（判断のサイクルごとに叩かない）。最新の成功（最後の足が前営業日）は取引日の終わりまで。
// 🔴 **最後の足が期待する前営業日でない成功（古い成功）も、覚えるのは <see cref="FailureRetryInterval"/> の間だけ**
//   （［2026-10-01 追記 / #1118］監査 🟡-3）。moomoo 側の前営業日の足の公開が遅れた日に、古い応答を取引日の終わりまで
//   抱えて「未提供」のまま 1 日回復しない形を塞ぐ。その間は古い応答を返す（計算側が「未提供」にする）。
//   臨時休場の翌営業日は 15 分ごとに撃ち直して「未提供」のままになる（銘柄ごとの撃ち直しで抑えられ、同じ銘柄の取り直しは
//   取得枠を増やさない＝#1117 の実測）。
// 🔴 **米国株だけ**を取る（日足 K 線の履歴源は米国株。ADR-0023 決定 5）。日本株は要求せず null。
// 🔴 **取得は銘柄ごとに 1 本ずつ**（銘柄 × 市場ごとのゲート。［2026-10-01 追記 / #1118］監査 🟡-4）。1 銘柄の取得が
//   発注執行の遅れで待たされても、他の銘柄の判断は待たない（全体を 1 本のゲートで直列化すると、遅い 1 銘柄の待ちが
//   監視銘柄の数だけ積み重なる）。同じ銘柄の同時の要求は 1 回にまとめる。1 取引日の取得回数は銘柄の数（＋撃ち直し）で抑えられる。
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

    private readonly ConcurrentDictionary<(string Symbol, Market Market), SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<(string Symbol, Market Market), Entry> _cache = new();

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

        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(key, out var cached) && cached.TradingDay == tradingDay)
            {
                // 最新の成功（最後の足が前営業日）は取引日の終わりまで。失敗・古い成功は撃ち直しの時刻まで。
                if (cached.RetryAt is not { } retryAt || now < retryAt)
                    return cached.Bars;
            }

            var (expectedPrevious, from, to) = RequestWindow(market, tradingDay);

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

            var result = Confirm(tradingDay, expectedPrevious, fetched);
            var confirmed = result.Bars;
            // 🔴 最後の足が期待する前営業日でない（公開の遅れ・臨時休場の翌日）成功は、失敗と同じく撃ち直しの時刻を置く。
            var stale = confirmed.Count == 0 || confirmed[^1].Date != expectedPrevious;
            _cache[key] = new Entry(tradingDay, result, stale ? now + FailureRetryInterval : null);
            if (stale)
            {
                logger.LogInformation(
                    "日足の最後の足が前営業日でない。{Minutes} 分後に撃ち直す（出来高は未提供）: {Symbol} tradingDay={TradingDay} expected={Expected}",
                    FailureRetryInterval.TotalMinutes, symbol, tradingDay, expectedPrevious);
            }
            logger.LogInformation(
                "日足を取得（前営業日まで・前復権）: {Symbol} tradingDay={TradingDay} bars={Bars} last={Last} droppedUnconfirmed={Dropped}",
                symbol, tradingDay, confirmed.Count, confirmed.Count > 0 ? confirmed[^1].Date : null,
                fetched.Count - confirmed.Count);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// FR-15, ADR-0048 決定 2, #1139, IADR-0479 決定 1: Stage 0 の判断時点（<paramref name="tradingDay"/>＝AsOf）より前の確定足を 1 回取る。
    /// 期間と切り方は本番（<see cref="GetConfirmedBarsAsync"/>）と同じ関数を使う。🔴 **キャッシュは読まない・書かない**
    /// （鍵は今の取引日であり、過去日の取得で本番の今日の取得を置き換えない）。米国株だけ。失敗は null（キャンセルは伝える）。
    /// </summary>
    public async Task<ConfirmedDailyBars?> GetConfirmedBarsAsOfAsync(
        string symbol, Market market, DateOnly tradingDay, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (market != Market.UnitedStates)
            return null;

        var (expectedPrevious, from, to) = RequestWindow(market, tradingDay);
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
            logger.LogWarning(ex, "Stage 0 の日足の取得で例外。出来高は未提供として記録します: {Symbol} asOf={AsOf}", symbol, tradingDay);
            return null;
        }

        if (fetched is null)
        {
            logger.LogInformation("Stage 0 の日足を取得できない（出来高は未提供）: {Symbol} asOf={AsOf}", symbol, tradingDay);
            return null;
        }

        var result = Confirm(tradingDay, expectedPrevious, fetched);
        logger.LogInformation(
            "Stage 0 の日足を取得（判断時点の前営業日まで・前復権）: {Symbol} asOf={AsOf} bars={Bars} last={Last} droppedAsOfOrLater={Dropped}",
            symbol, tradingDay, result.Bars.Count, result.Bars.Count > 0 ? result.Bars[^1].Date : null,
            fetched.Count - result.Bars.Count);
        return result;
    }

    // #1118, #1139, IADR-0467 決定 3, IADR-0479 決定 1: 要求の期間（本番と Stage 0 で共有）。前営業日の 45 暦日前〜取引日の前日。
    internal static (DateOnly ExpectedPrevious, DateOnly From, DateOnly To) RequestWindow(Market market, DateOnly tradingDay)
    {
        var expectedPrevious = MarketTradingDays.PreviousTradingDay(market, tradingDay);
        return (expectedPrevious, expectedPrevious.AddDays(-LookbackCalendarDays), tradingDay.AddDays(-1));
    }

    // 🔴 #1118, #1139: 取引日以降の足（本番は未確定の当日足、Stage 0 は判断時点以降の足＝先読み）を捨てる。
    // 重複した日付は先の 1 本だけを残し、昇順に並べる（本番と Stage 0 で共有）。
    internal static ConfirmedDailyBars Confirm(DateOnly tradingDay, DateOnly expectedPrevious, IEnumerable<DailyBar> fetched) =>
        new(tradingDay, expectedPrevious, [.. fetched
            .Where(b => b.Date < tradingDay)
            .GroupBy(b => b.Date)
            .Select(g => g.First())
            .OrderBy(b => b.Date)]);

    public void Dispose()
    {
        foreach (var gate in _gates.Values)
            gate.Dispose();
    }

    // RetryAt が null ＝ 最新の成功（取引日の終わりまで使う）。値あり ＝ 失敗（Bars が null）または古い成功で、その時刻から撃ち直す。
    private sealed record Entry(DateOnly TradingDay, ConfirmedDailyBars? Bars, DateTimeOffset? RetryAt);
}
