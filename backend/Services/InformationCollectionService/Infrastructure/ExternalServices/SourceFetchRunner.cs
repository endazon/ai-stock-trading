using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Domain;
using Microsoft.Extensions.Logging;

namespace InformationCollectionService.Infrastructure.ExternalServices;

// FR-01, ADR-0004, ADR-0020 決定3, IADR-0064: 有効化された情報源を束ねて 1 巡回＝1 収集として扱う。
// 1 ソースの障害・キー切れが他ソースと巡回を巻き込まないよう、失敗はソース単位で隔離する。
//
// 🔴 **失敗をログするだけにしない。** 旧 CompositeInformationSource は障害をログして捨てていたため、
// 「どの区分のソースが落ちたか」を欠測判定へ渡せなかった（ADR-0020 決定3 が成立しない）。
// 本型は**ソース単位の成否（SourceOutcome）を返す**——ログは人が読むためのもので、統制の入力にはならない。
public sealed class SourceFetchRunner(
    IReadOnlyList<NamedInformationSource> sources,
    ILogger<SourceFetchRunner> logger) : ISourceFetcher
{
    /// <summary>
    /// 有効化されている情報源の名前（構成束縛の検証・診断用）。
    /// <b>構成キーの綴り違いは「静かに全ソース無効（ゼロ件収集）」として現れる</b>ため、
    /// 合成の中身を外から確かめられるようにしておく。
    /// </summary>
    public IReadOnlyList<string> SourceNames => [.. sources.Select(s => s.Name)];

    public async Task<SourceFetchResult> FetchAllAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<RawInformationItem>();
        var outcomes = new List<SourceOutcome>(sources.Count);

        foreach (var source in sources)
        {
            try
            {
                items.AddRange(await source.Source.FetchAsync(cancellationToken).ConfigureAwait(false));
                outcomes.Add(SourceOutcome.Ok(source.Name));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 停止要求は握りつぶさない（巡回ごと畳む）。
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "情報源 {Source} の取得に失敗したため、このソースを欠測として扱います。", source.Name);
                outcomes.Add(SourceOutcome.Failed(source.Name));
            }
        }

        return new SourceFetchResult(items, outcomes);
    }
}
