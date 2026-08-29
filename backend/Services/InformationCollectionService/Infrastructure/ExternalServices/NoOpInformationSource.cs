using InformationCollectionService.Features.InformationCollection;

namespace InformationCollectionService.Infrastructure.ExternalServices;

// FR-01, IADR-0022: 何も取得しない安全既定の情報源。外部 API に接続しない（費用/レート違反を起こさない）。
// 実情報源は構成で明示有効化する。
public sealed class NoOpInformationSource : IInformationSource
{
    private static readonly IReadOnlyList<RawInformationItem> Empty = [];

    public Task<IReadOnlyList<RawInformationItem>> FetchAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Empty);
}
