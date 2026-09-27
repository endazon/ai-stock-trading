using Microsoft.Extensions.Logging;
using ReportService.Domain;
using ReportService.Features.Reports;

namespace ReportService.Infrastructure.ExternalServices;

// NFR, FR-06, FR-10, FR-11, IADR-0429, MSP:ADR-0029, IADR-0284 決定 5（段 3）, IADR-0445, #1059 (#753):
// 損切りの実行機構の解決結果を監査台帳から **gRPC 生成クライアント**（`AuditEventsRead/GetEventsByType`）で引く `IStopLossMethodResolutionSource` の 2 つ目の実装。
// REST 実装（HttpStopLossMethodResolutionSource）と並走する（**既定は REST**。`Audit:Grpc` を宣言したときだけ選ばれる）。
//
// 🔴 **照会の窓・引く種別・記録の解釈は REST と同じ 1 つ**（`HttpStopLossMethodResolutionSource.Window` / `WantedTypes` / `Build`）。
// 🔴 **倒す向きは REST と同じ null（未供給）**。「記録なし」へ倒さない（照会の窓は REST と同じく報告期間の前後 1 日を含む）。
public sealed class GrpcStopLossMethodResolutionSource(AuditGrpcTransport transport, ILogger<GrpcStopLossMethodResolutionSource> logger) : IStopLossMethodResolutionSource
{
    public Task<StopLossMethodResolutionFeed?> GetResolutionsAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default) =>
        transport.ReadAsync<StopLossMethodResolutionFeed>(
            "損切りの実行機構の解決結果",
            HttpStopLossMethodResolutionSource.Window(fromInclusive, toInclusive),
            HttpStopLossMethodResolutionSource.WantedTypes,
            entries => HttpStopLossMethodResolutionSource.Build(entries, logger),
            logger,
            cancellationToken);
}
