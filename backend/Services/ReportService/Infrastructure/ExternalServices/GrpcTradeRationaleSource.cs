using Microsoft.Extensions.Logging;
using ReportService.Features.Reports;

namespace ReportService.Infrastructure.ExternalServices;

// NFR, FR-16, FR-11, IADR-0269, MSP:ADR-0029, IADR-0284 決定 5（段 3）, IADR-0445, #1059 (#753):
// 取引判断の根拠を監査台帳から **gRPC 生成クライアント**（`AuditEventsRead/GetEventsByType`）で引く `ITradeRationaleSource` の 2 つ目の実装。
// REST 実装（HttpTradeRationaleSource）と並走する（**既定は REST**。`Audit:Grpc` を宣言したときだけ選ばれる）。
//
// 🔴 **照会の窓・引く種別・記録の解釈は REST と同じ 1 つ**（`HttpTradeRationaleSource.Window` / `WantedTypes` / `Build`）。
// 🔴 **倒す向きは REST と同じ null（未供給）**。空の辞書（「根拠なし」）へ倒さない。
public sealed class GrpcTradeRationaleSource(AuditGrpcTransport transport, ILogger<GrpcTradeRationaleSource> logger) : ITradeRationaleSource
{
    public Task<IReadOnlyDictionary<Guid, string>?> GetRationalesAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default) =>
        transport.ReadAsync<IReadOnlyDictionary<Guid, string>>(
            "取引判断の根拠",
            HttpTradeRationaleSource.Window(fromInclusive, toInclusive),
            HttpTradeRationaleSource.WantedTypes,
            entries => HttpTradeRationaleSource.Build(entries, logger),
            logger,
            cancellationToken);
}
