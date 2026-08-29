
namespace InformationCollectionService.Features.InformationCollection;

// FR-01, ADR-0004: 情報源アダプタのポート。案A+ の各ソースを実装差し替えで切り替える。既定は外部接続しない no-op（IADR-0022）。
public interface IInformationSource
{
    Task<IReadOnlyList<RawInformationItem>> FetchAsync(CancellationToken cancellationToken = default);
}
