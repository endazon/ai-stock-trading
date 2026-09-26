using AiStockTrading.Shared.Contracts.Trading;

namespace InformationCollectionService.Features.InformationCollection;

// FR-01, FR-13, #1015, IADR-0435: 市場監視（権威源）の監視銘柄を読むポート。取引判断の定時サイクルが判断対象を決めるのと
// 同じ口（GET /monitor/watchlist・OwnerOrService。IADR-0095）であり、**判断対象と収集対象をずらさない**ために読む。
//
// 🔴 **原則 A: 不明は空ではない。** 読めなかったときは null を返す。空の一覧は「監視銘柄が 0 件」という事実であり、
// 失敗を空へ倒すと「何も収集しない」が黙って成立してしまう。
public interface IWatchlistReader
{
    /// <summary>監視銘柄の一覧（空＝0 件）。読めなければ <c>null</c>（不明）。</summary>
    Task<IReadOnlyList<WatchedSymbol>?> ReadAsync(CancellationToken cancellationToken = default);
}

// 監視銘柄（銘柄コード・市場）。送り手 MarketMonitorService.Domain.MonitoredSymbol と同形。
public sealed record WatchedSymbol(string Symbol, Market Market);
