using Microsoft.Extensions.Logging;
using ReportService.Domain;
using ReportService.Features.Reports;

namespace ReportService.Infrastructure.ExternalServices;

// NFR, FR-06, FR-10, FR-11, IADR-0422, MSP:ADR-0029, IADR-0284 決定 5（段 3）, IADR-0445, #1059 (#753):
// 承認の記録（損切りの実行機構）を監査台帳から **gRPC 生成クライアント**（`AuditEventsRead/GetEventsByType`）で引く `IStopLossMethodUsageSource` の 2 つ目の実装。
// REST 実装（HttpStopLossMethodUsageSource）と並走する（**既定は REST**。`Audit:Grpc` を宣言したときだけ選ばれる）。
//
// 🔴 **照会の窓・引く種別・記録の解釈は REST と同じ 1 つ**（`HttpStopLossMethodUsageSource.Window` / `WantedTypes` / `Build`）。
// 🔴 **倒す向きは REST と同じ null（未供給）**。「承認なし」へ倒さない。
public sealed class GrpcStopLossMethodUsageSource(AuditGrpcTransport transport, ILogger<GrpcStopLossMethodUsageSource> logger) : IStopLossMethodUsageSource
{
    public Task<StopLossMethodUsage?> GetUsageAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default) =>
        transport.ReadAsync<StopLossMethodUsage>(
            "承認の記録（損切りの実行機構）",
            HttpStopLossMethodUsageSource.Window(fromInclusive, toInclusive),
            HttpStopLossMethodUsageSource.WantedTypes,
            entries => HttpStopLossMethodUsageSource.Build(entries, logger),
            logger,
            cancellationToken);
}
