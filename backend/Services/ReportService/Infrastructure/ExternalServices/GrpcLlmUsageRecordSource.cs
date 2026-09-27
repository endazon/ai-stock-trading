using Microsoft.Extensions.Logging;
using ReportService.Domain;
using ReportService.Features.Reports;

namespace ReportService.Infrastructure.ExternalServices;

// NFR, FR-06, FR-11, FR-16, IADR-0254, MSP:ADR-0029, IADR-0284 決定 5（段 3）, IADR-0445, #1059 (#753):
// LLM 利用実績を監査台帳から **gRPC 生成クライアント**（`AuditEventsRead/GetEventsByType`）で引く `ILlmUsageRecordSource` の 2 つ目の実装。
// REST 実装（HttpLlmUsageRecordSource）と並走する（**既定は REST**。`Audit:Grpc` を宣言したときだけ選ばれる）。
//
// 🔴 **照会の窓・引く種別・記録の解釈は REST と同じ 1 つ**（`HttpLlmUsageRecordSource.Window` / `WantedTypes` / `Build`）。
// 🔴 **倒す向きは REST と同じ null（未供給）**。費用 0 円・スキップ 0 件へ倒さない（LLM は本番で実際に呼ばれている）。
public sealed class GrpcLlmUsageRecordSource(AuditGrpcTransport transport, ILogger<GrpcLlmUsageRecordSource> logger) : ILlmUsageRecordSource
{
    public Task<LlmUsageRecord?> GetUsageAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default) =>
        transport.ReadAsync<LlmUsageRecord>(
            "LLM 利用実績",
            HttpLlmUsageRecordSource.Window(fromInclusive, toInclusive),
            HttpLlmUsageRecordSource.WantedTypes,
            entries => HttpLlmUsageRecordSource.Build(entries, logger),
            logger,
            cancellationToken);
}
