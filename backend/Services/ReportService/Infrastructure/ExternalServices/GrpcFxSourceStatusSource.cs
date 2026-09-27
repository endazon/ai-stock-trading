using Microsoft.Extensions.Logging;
using ReportService.Domain;
using ReportService.Features.Reports;

namespace ReportService.Infrastructure.ExternalServices;

// NFR, FR-06, FR-10, FR-11, IADR-0199, MSP:ADR-0029, IADR-0284 決定 5（段 3）, IADR-0445, #1059 (#753):
// 為替の情報源の状態を監査台帳から **gRPC 生成クライアント**（`AuditEventsRead/GetEventsByType`）で引く `IFxSourceStatusSource` の 2 つ目の実装。
// REST 実装（HttpFxSourceStatusSource）と並走する（**既定は REST**。`Audit:Grpc` を宣言したときだけ選ばれる）。
//
// 🔴 **照会の窓・引く種別・記録の解釈は REST と同じ 1 つ**（`HttpFxSourceStatusSource.Window` / `WantedTypes` / `Build`）。
// 🔴 **倒す向きは REST と同じ null（未供給）**。空列（「切替なし」）へ倒さない（為替のイベントは本番で実際に発行されている）。
public sealed class GrpcFxSourceStatusSource(AuditGrpcTransport transport, ILogger<GrpcFxSourceStatusSource> logger) : IFxSourceStatusSource
{
    public Task<FxSourceStatus?> GetStatusAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default) =>
        transport.ReadAsync<FxSourceStatus>(
            "為替の情報源の状態",
            HttpFxSourceStatusSource.Window(fromInclusive, toInclusive),
            HttpFxSourceStatusSource.WantedTypes,
            entries => HttpFxSourceStatusSource.Build(entries, logger),
            logger,
            cancellationToken);
}
