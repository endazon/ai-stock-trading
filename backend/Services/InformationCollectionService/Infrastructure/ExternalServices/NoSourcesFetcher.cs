using InformationCollectionService.Features.InformationCollection;

namespace InformationCollectionService.Infrastructure.ExternalServices;

// FR-01, IADR-0022: 情報源が 1 つも有効化されていないときの安全既定。**外部へ接続しない。**
//
// 🔴 **成否を 1 件も返さない**（空の Outcomes）。0 件の試行を「全滅」と読ませないためである——
// 未構成と欠測は別の事実であり、混同すると外部接続しない既定のままで毎サイクルが縮退する。
public sealed class NoSourcesFetcher : ISourceFetcher
{
    public Task<SourceFetchResult> FetchAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(SourceFetchResult.Empty);
}
