using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.MarketMonitor.Worker.Composable.Adapters;

// FR-03: IMarketDataSource の暫定実装。moomoo のリアルタイム市況アダプタは #13 と併せて実装する。
// それまでは価格取得不可（null）を返す＝巡回は動くがイベントは発行されない。差し替え漏れ検知のため初回に 1 回警告する。
internal sealed class PlaceholderMarketDataSource(ILogger<PlaceholderMarketDataSource> logger) : IMarketDataSource
{
    private int _warned;

    public Task<Quote?> GetLatestQuoteAsync(string symbol, Market market, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            logger.LogWarning(
                "PlaceholderMarketDataSource を使用中: リアルタイム市況（moomoo・#13）が入るまで価格を取得できず、監視イベントは発行されません。");
        }

        return Task.FromResult<Quote?>(null);
    }
}
