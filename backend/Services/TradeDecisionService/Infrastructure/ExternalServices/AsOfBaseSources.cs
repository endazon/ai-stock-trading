using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-15, #1308, IADR-0528 決定2・3: as-of の基底の照会を撃ち直す共通の規則。
// 照会の失敗（null・例外）は間を置いて数回撃ち直し、それでも取れなければ AsOfInputUnavailableException（記録を止める）。
// 🔴 「当時無かった」（休場・確定日報なし）は撃ち直さない（呼び出し側が null 以外の答えとして返す）。
internal static class AsOfRetry
{
    /// <summary>撃ち直しの回数（初回を含む）。</summary>
    public const int Attempts = 3;

    /// <summary>撃ち直しの間隔の既定（発注執行の自制〔60 秒に 25 回〕と報告書の一時の障害が回復する長さ）。</summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(20);

    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<(bool Ok, T Value)>> attempt,
        string what,
        TimeSpan delay,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken cancellationToken)
        where T : class?
    {
        Exception? last = null;
        for (var i = 1; i <= Attempts; i++)
        {
            try
            {
                var (ok, value) = await attempt(cancellationToken).ConfigureAwait(false);
                if (ok)
                    return value;
            }
            // 送り手の契約違反など、撃ち直しても変わらない失敗（AsOfInputUnavailableException）はそのまま伝える。
            catch (Exception ex) when (ex is not AsOfInputUnavailableException
                && (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
            {
                last = ex;
            }

            if (i < Attempts)
            {
                logger.LogWarning("Stage 0 の as-of 入力: {What} を取れませんでした（{Attempt}/{Attempts}）。{Delay} 後に撃ち直します。",
                    what, i, Attempts, delay);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new AsOfInputUnavailableException($"{what} を {Attempts} 回撃っても取れませんでした。記録を止めます。", last);
    }
}

// FR-04, FR-15, ADR-0033 決定2, #1308, IADR-0528 決定2: 判断日の価格の口。発注執行の日足（前復権。IADR-0467 の口と同じ
// `IDailyBarsSource`）を `[AsOf − 10 暦日, AsOf]` で取り、AsOf の足の終値を参照価格、AsOf より前の最後の足の終値を前日終値にする。
// - AsOf の足が無い（取れたが無い）＝休場 → Close null（判断しない）。
// - 取れない（null・例外）→ 撃ち直してから例外（記録を止める）。
// - 🔴 判断日が今日（市場の取引日）以降なら、その足は未確定である。使わずに例外にする（未確定の足で判断させない）。
public sealed class DailyBarsAsOfPriceSource(
    IDailyBarsSource source,
    TimeProvider timeProvider,
    ILogger<DailyBarsAsOfPriceSource> logger,
    TimeSpan? retryDelay = null)
    : IAsOfPriceSource
{
    /// <summary>判断日から遡って求める暦日（前日終値を休場の多い週でも得られる幅）。</summary>
    public const int LookbackCalendarDays = 10;

    public async Task<AsOfPriceReading> GetAsync(
        string symbol, Market market, DateOnly asOf, CancellationToken cancellationToken = default)
    {
        var tradingDay = MarketTradingDays.TradingDateOf(market, timeProvider.GetUtcNow());
        if (asOf >= tradingDay)
        {
            throw new AsOfInputUnavailableException(
                $"判断日 {asOf} は今日の取引日 {tradingDay} 以降です（足が確定していない）。記録の期間を過去の日に限ってください。");
        }

        var bars = await AsOfRetry.RunAsync<IReadOnlyList<DailyBar>?>(
            async ct =>
            {
                var fetched = await source.FetchAsync(symbol, market, asOf.AddDays(-LookbackCalendarDays), asOf, ct).ConfigureAwait(false);
                return (fetched is not null, fetched);
            },
            $"{symbol} の {asOf} の日足",
            retryDelay ?? AsOfRetry.DefaultDelay,
            timeProvider,
            logger,
            cancellationToken).ConfigureAwait(false);

        var ordered = bars!.Where(b => b.Date <= asOf).OrderBy(b => b.Date).ToList();
        var close = ordered.LastOrDefault(b => b.Date == asOf);
        var previous = ordered.LastOrDefault(b => b.Date < asOf);
        return new AsOfPriceReading(
            close is null ? null : new DatedPrice(close.Date, close.Close),
            previous is null ? null : new DatedPrice(previous.Date, previous.Close));
    }
}
