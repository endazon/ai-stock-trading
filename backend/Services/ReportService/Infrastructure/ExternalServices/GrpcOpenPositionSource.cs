using ReportService.Domain;
using ReportService.Features.Reports;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace ReportService.Infrastructure.ExternalServices;

// NFR, FR-06, FR-16, MSP:ADR-0029, IADR-0284 決定 5（段 2）, IADR-0269, IADR-0408, IADR-0427, #997 (#753):
// 日報 §3 の建玉を **gRPC 生成クライアント**（`RiskControlsRead/GetOpenPositions`）で照会する `IOpenPositionSource` の
// 2 つ目の実装。REST 実装（HttpOpenPositionSource）と並走する（**既定は REST**）。
//
// 🔴 **倒す向きは REST と同じ null（未供給）**。空列（建玉なし）と混ぜない。
// 🔴 **行の扱いは REST と同じ 1 つ**（`HttpOpenPositionSource.Interpret`: 1 行でも読めなければ §3 全体を未供給。#957）。
// 🔴 **原則 A**: 欠落・未指定は null（行は識別できない＝未供給へ）。0 円・日本・買いと読まない。
public sealed class GrpcOpenPositionSource(RiskManagementGrpcTransport transport, ILogger<GrpcOpenPositionSource> logger)
    : IOpenPositionSource
{
    public async Task<IReadOnlyList<ReportPosition>?> GetOpenPositionsAsync(CancellationToken cancellationToken = default)
    {
        var response = await transport.CallAsync(
            "建玉",
            "**未供給として扱います**（「建玉なし」とは書きません）。",
            (client, options) => client.GetOpenPositionsAsync(new Proto.GetOpenPositionsRequest(), options),
            cancellationToken).ConfigureAwait(false);
        if (response is null)
            return null;

        List<HttpOpenPositionSource.OpenPositionDto?> rows;
        try
        {
            rows = [.. response.Positions.Select(ToRow)];
        }
        catch (FormatException ex)
        {
            logger.LogWarning(ex, "建玉の gRPC 応答を読めません（10 進の書式）。**未供給として扱います**。");
            return null;
        }

        return HttpOpenPositionSource.Interpret(rows, logger);
    }

    internal static HttpOpenPositionSource.OpenPositionDto? ToRow(Proto.OpenPositionRow p) => new(
        p.HasSymbol ? p.Symbol : null,
        RiskManagementWire.Market(p.Market),
        RiskManagementWire.Side(p.Side),
        p.HasQuantity ? p.Quantity : null,
        RiskManagementWire.Decimal(p.HasEntryPrice, p.EntryPrice),
        RiskManagementWire.Decimal(p.HasStopLossPrice, p.StopLossPrice));
}
