using InformationCollectionService.Features.InformationCollection;

namespace InformationCollectionService.Infrastructure.ExternalServices;

// FR-01, FR-02, FR-13, #1015, IADR-0435: 取得の前に Finnhub の対象銘柄を監視銘柄から決め直す装飾。
// **1 巡回に 1 回だけ**照会する（現在値と企業ニュースの 2 ソースが別々に照会すると、巡回の途中で集合が食い違い得る）。
// 市場監視に結線し、かつ Finnhub 系のソースが有効なときだけ Program.cs が挟む（それ以外は照会しない）。
// FR-01, ADR-0043（計画）決定 3, #1030, IADR-0437: 決め直した後の対象の数を <paramref name="onRefreshed"/> へ渡す
// （Program.cs が日次要求の見積りをその巡回の対象の数で記録し直す。対象は巡回ごとに変わるため、起動時の値では数えない）。
public sealed class WatchlistFollowingSourceFetcher(
    ISourceFetcher inner, FinnhubSymbolSelector selector, Action<int>? onRefreshed = null) : ISourceFetcher
{
    /// <summary>装飾している取得器（構成束縛の検証・診断用）。</summary>
    public ISourceFetcher Inner => inner;

    public async Task<SourceFetchResult> FetchAllAsync(CancellationToken cancellationToken = default)
    {
        await selector.RefreshAsync(cancellationToken).ConfigureAwait(false);
        onRefreshed?.Invoke(selector.Current.Count);
        return await inner.FetchAllAsync(cancellationToken).ConfigureAwait(false);
    }
}
