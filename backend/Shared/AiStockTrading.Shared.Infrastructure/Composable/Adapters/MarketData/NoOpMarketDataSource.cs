using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;

// FR-03, FR-10, IADR-0065: IMarketDataSource の既定実装＝常に取得不可（null）。
// 実市況（OpenD 市況サブスクリプション or Finnhub 共有化）は後続 issue＋手動 opt-in の live 検証へ分離しており、
// 既定では実接続しない（fail-safe）。取得不可のため、監視イベントは発行されず、含み損益・DD は 0 に倒れる。
// 差し替え漏れ検知のため初回に 1 回だけ警告する（巡回ごとのログ氾濫を避ける）。
public sealed class NoOpMarketDataSource(ILogger<NoOpMarketDataSource> logger) : IMarketDataSource
{
    private int _warned;

    public Task<Quote?> GetLatestQuoteAsync(string symbol, Market market, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            logger.LogWarning(
                "NoOpMarketDataSource を使用中: 実市況が未接続のため現在値を取得できません。監視イベントは発行されず、含み損益・ドローダウンは 0 として扱われます（IADR-0065）。");
        }

        return Task.FromResult<Quote?>(null);
    }
}
