using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.InformationCollection.Application.State;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.InformationCollection.Worker.Composable.Adapters;

// FR-01, ADR-0004, IADR-0061: 案A+ は複数情報源の組み合わせ（開示=EDINET/SEC EDGAR、マクロ=FRED/日銀 等）であるため、
// 有効化された各ソースを束ねて 1 巡回＝1 収集として扱う（1 巡回＝1 InformationCollected の前提を保つ）。
// 1 ソースの障害・キー切れが他ソースと巡回を巻き込まないよう、失敗はソース単位で隔離してログする（欠測検知）。
internal sealed class CompositeInformationSource(
    IReadOnlyList<IInformationSource> sources,
    ILogger<CompositeInformationSource> logger) : IInformationSource
{
    public async Task<IReadOnlyList<RawInformationItem>> FetchAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<RawInformationItem>();

        foreach (var source in sources)
        {
            try
            {
                items.AddRange(await source.FetchAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 停止要求は握りつぶさない（巡回ごと畳む）。
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "情報源 {Source} の取得に失敗したため、このソースを欠測として今回の巡回から除外します。", source.GetType().Name);
            }
        }

        return items;
    }
}
