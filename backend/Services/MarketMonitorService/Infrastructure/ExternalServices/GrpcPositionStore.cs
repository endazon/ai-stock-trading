using AiStockTrading.Shared.Contracts.Observability;
using MarketMonitorService.Domain;
using MarketMonitorService.Features.MarketMonitor;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace MarketMonitorService.Infrastructure.ExternalServices;

// NFR, FR-03, FR-10, MSP:ADR-0029, IADR-0284 決定 5（段 2）, IADR-0427, #997 (#753):
// 保有ポジションを **gRPC 生成クライアント**（`RiskControlsRead/GetOpenPositions`）で照会する `IPositionStore` の
// 2 つ目の実装。REST 実装（HttpPositionStore）と並走し、選ぶのは Program.cs（`RiskManagement:Grpc`。**既定は REST**）。
//
// 🔴 **行ごとの扱いは REST 実装と同じ 1 つ**（`HttpPositionStore.Classify`。#957 / IADR-0399 の「識別できない行は
// 評価に渡さない・ラインが無い行は近似で評価する・Critical と計器で声に出す」）。ここは proto を同じ nullable の行へ写すだけ。
// 🔴 **原則 A**: 欠落・未指定は null。**損切りラインの欠落を 0 と読まない**（ロングは発火せず、含み益のショートは
// 毎巡回発火する）。列挙の 0 を日本・買いと読まない。
// 🔴 **fail-safe の向きは REST と同じ**: 取得できない（status・deadline）は空列（損切り検知対象なし）で Warning、
// 応答を読めない（10 進の書式）は REST の「200 の本文が一覧として読めない」と同じく空列のまま Critical と計器で出す。
public sealed class GrpcPositionStore(
    RiskManagementGrpcTransport transport,
    BusinessMetrics metrics,
    ILogger<GrpcPositionStore> logger)
    : IPositionStore
{
    internal const string GrpcSource = "gRPC RiskControlsRead/GetOpenPositions";

    private static readonly IReadOnlyCollection<HeldPosition> Empty = [];

    public async Task<IReadOnlyCollection<HeldPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken = default)
    {
        var response = await transport.CallAsync(
            "保有ポジション",
            "空列（損切り検知対象なし）に倒します。",
            (client, options) => client.GetOpenPositionsAsync(new Proto.GetOpenPositionsRequest(), options),
            cancellationToken).ConfigureAwait(false);
        if (response is null)
            return Empty;

        List<HttpPositionStore.OpenPositionDto?> rows;
        try
        {
            rows = [.. response.Positions.Select(ToRow)];
        }
        catch (FormatException ex)
        {
            HttpPositionStore.ReportUnreadableResponse(metrics, logger, GrpcSource, ex.Message);
            return Empty;
        }

        return HttpPositionStore.Classify(rows, metrics, logger, GrpcSource);
    }

    // 線上 → REST と同じ nullable の行（欠落・未指定は null）。
    internal static HttpPositionStore.OpenPositionDto ToRow(Proto.OpenPositionRow p) => new(
        p.HasSymbol ? p.Symbol : null,
        RiskManagementWire.Market(p.Market),
        RiskManagementWire.Side(p.Side),
        p.HasQuantity ? p.Quantity : null,
        RiskManagementWire.Decimal(p.HasEntryPrice, p.EntryPrice),
        RiskManagementWire.Decimal(p.HasStopLossPrice, p.StopLossPrice));
}
