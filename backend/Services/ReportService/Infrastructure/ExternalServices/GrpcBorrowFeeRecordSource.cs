using Microsoft.Extensions.Logging;
using ReportService.Domain;
using ReportService.Features.Reports;

namespace ReportService.Infrastructure.ExternalServices;

// NFR, FR-06, FR-11, MSP:ADR-0029, IADR-0284 決定 5（段 3）, IADR-0445, #1059 (#753):
// 借株料の記録を監査台帳から **gRPC 生成クライアント**（`AuditEventsRead/GetEventsByType`）で引く `IBorrowFeeRecordSource` の 2 つ目の実装。
// REST 実装（HttpBorrowFeeRecordSource）と並走する（**既定は REST**。`Audit:Grpc` を宣言したときだけ選ばれる）。
//
// 🔴 **照会の窓・引く種別・記録の解釈は REST と同じ 1 つ**（`HttpBorrowFeeRecordSource.Window` / `WantedTypes` / `Build`）。
// 🔴 **倒す向きは REST と同じ null（未供給）**。借株コスト 0 円へ倒さない（未計上を 0 円として合計へ混ぜない向きと同じ）。
public sealed class GrpcBorrowFeeRecordSource(AuditGrpcTransport transport, ILogger<GrpcBorrowFeeRecordSource> logger) : IBorrowFeeRecordSource
{
    public Task<BorrowFeeRecord?> GetBorrowFeesAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default) =>
        transport.ReadAsync<BorrowFeeRecord>(
            "借株料の記録",
            HttpBorrowFeeRecordSource.Window(fromInclusive, toInclusive),
            HttpBorrowFeeRecordSource.WantedTypes,
            entries => HttpBorrowFeeRecordSource.Build(entries, logger),
            logger,
            cancellationToken);
}
