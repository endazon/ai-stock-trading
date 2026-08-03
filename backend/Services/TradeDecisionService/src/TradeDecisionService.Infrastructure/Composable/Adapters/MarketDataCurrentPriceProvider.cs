using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Application.State;
using AiStockTrading.Shared.Contracts.Ports;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;

// FR-02, FR-04, FR-10, IADR-0099: 判断文脈の現在値供給アダプタ。#158（IADR-0068）で共有化された現在値の権威ポート
// IMarketDataSource を包み、鮮度（AsOf と現在時刻の差）を検査して権威ある現在値を判断サービスへ渡す。
//
// enabled は「現在値ソースが実結線されているか」（Program.cs が MarketDataSourceFactory の生成物が no-op でないことで
// 判定して渡す）。false（未有効化・no-op）のときは実ソースを呼ばず即 null を返し、判断側の fail-safe ゲート
// （IsEnabled=false なら発注抑止しない・現行挙動）に委ねる。
//
// fail-safe: 取得不可（null）・鮮度切れは null（現在値なし）へ倒す。例外は判断サービス側の GetCurrentPriceSafeAsync が
// 握るため、ここでは通常の取得失敗（ソースが null を返す）だけを扱う。
internal sealed class MarketDataCurrentPriceProvider(
    IMarketDataSource source,
    bool enabled,
    IClock clock,
    TimeSpan maxStaleness,
    ILogger<MarketDataCurrentPriceProvider> logger) : ICurrentPriceProvider
{
    public bool IsEnabled => enabled;

    public async Task<decimal?> GetCurrentPriceAsync(
        DecisionTrigger trigger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        // 未有効化（no-op ソース）は実取得を試みない＝現在値なし。判断側は IsEnabled=false のため発注抑止しない。
        if (!enabled)
        {
            return null;
        }

        var quote = await source
            .GetLatestQuoteAsync(trigger.Symbol, trigger.Market, cancellationToken)
            .ConfigureAwait(false);

        // 取得不可（null）＝現在値なし（有効化時は判断側で発注抑止＝安全側）。
        if (quote is null)
        {
            return null;
        }

        // IADR-0066/0099: 鮮度切れ（保持期限超過）は「取得不可」として扱い現在値なしへ倒す。古い価格で発注しないため。
        var age = clock.UtcNow - quote.AsOf;
        if (age > maxStaleness)
        {
            logger.LogInformation(
                "現在値が鮮度切れのため現在値なしとして扱う: {Symbol} asOf={AsOf} age={AgeSeconds}s max={MaxSeconds}s",
                trigger.Symbol, quote.AsOf, age.TotalSeconds, maxStaleness.TotalSeconds);
            return null;
        }

        return quote.Price;
    }
}
