using TradeDecisionService.Features.TradeDecision;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Configuration;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-02, IADR-0023/0095: 定時サイクルで評価する監視銘柄を構成（TradeCycle:Watchlist）から供給する。
// 権威源は市場監視（#10 MarketMonitor の watchlist・IADR-0088）であり、本アダプタは権威源が**未結線**
// （MarketMonitor:BaseUrl・MarketMonitor:Grpc のどちらも無い）ときの後方互換としてだけ使う。
// #1134, IADR-0475: 結線時の照会失敗の代替には使わない（以前は IADR-0095 決定 3 の fail-safe 既定だった）。
public sealed class ConfigurationWatchlistProvider(IConfiguration configuration) : IWatchlistProvider
{
    public Task<IReadOnlyList<WatchedSymbol>?> GetWatchlistAsync(CancellationToken cancellationToken = default)
    {
        var entries = configuration.GetSection("TradeCycle:Watchlist").Get<List<WatchlistEntry>>() ?? [];
        IReadOnlyList<WatchedSymbol> result = [.. entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Symbol))
            .Select(e => new WatchedSymbol(e.Symbol!, e.Market))];
        return Task.FromResult<IReadOnlyList<WatchedSymbol>?>(result);
    }

    // FR-04, #1034, IADR-0440 決定 2: 構成の固定リストは権威源ではない（未結線の後方互換）。判断のプロンプトへは
    // 「判断時点の監視銘柄」として載せず、常に null（不明）を返す。
    public Task<IReadOnlyList<WatchedSymbol>?> GetAuthoritativeWatchlistAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WatchedSymbol>?>(null);

    // 構成バインド用（Market は列挙名でバインドされる）。
    private sealed class WatchlistEntry
    {
        public string? Symbol { get; set; }

        public Market Market { get; set; }
    }
}
