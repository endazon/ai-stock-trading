using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using ReportService.Domain;
using ReportService.Features.Reports;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace ReportService.Infrastructure.ExternalServices;

// NFR, FR-06, FR-16, MSP:ADR-0029, IADR-0427, IADR-0493 決定 2, #1181:
// 期間開始時点の在庫を **gRPC 生成クライアント**（`RiskControlsRead/GetOpeningInventory`）で照会する `IOpeningInventorySource` の
// 2 つ目の実装。REST 実装（HttpOpeningInventorySource）と並走し、選ぶのは Program.cs（`RiskManagement:Grpc`。**既定は REST**）。
//
// 🔴 **倒す向きは REST と同じ null（照会できていない）**。門・観測・deadline・再試行は輸送（RiskManagementGrpcTransport）が持つ。
// 🔴 **写しは REST と同じ解釈**（`HttpOpeningInventorySource.Interpret`）。必須の項目が欠けた行は既定値で作らず応答全体を null にする。
public sealed class GrpcOpeningInventorySource(
    RiskManagementGrpcTransport transport, ILogger<GrpcOpeningInventorySource> logger)
    : IOpeningInventorySource
{
    public async Task<IReadOnlyList<OpeningLot>?> GetOpeningInventoryAsync(
        Market market, DateOnly beforeTradingDay, CancellationToken cancellationToken = default)
    {
        var response = await transport.CallAsync(
            "期間開始時点の在庫",
            "未供給として報告書を続けます。",
            (client, options) => client.GetOpeningInventoryAsync(
                new Proto.GetOpeningInventoryRequest
                {
                    Market = ToProto(market),
                    Before = RiskManagementWire.Wire(beforeTradingDay),
                },
                options),
            cancellationToken).ConfigureAwait(false);
        if (response is null)
            return null;

        try
        {
            var lots = HttpOpeningInventorySource.Interpret(response.Lots.Select(ToRow), market);
            if (lots is null)
            {
                logger.LogError(
                    "期間開始時点の在庫の gRPC 応答に必須の項目の欠けた行・市場の食い違う行がありました（{Rows} 行中）。"
                        + "送り手との契約の食い違いとみなし、未供給として報告書を続けます。",
                    response.Lots.Count);
            }

            return lots;
        }
        catch (FormatException ex)
        {
            logger.LogWarning(ex, "期間開始時点の在庫の gRPC 応答を読めません（10 進の書式）。未供給として報告書を続けます。");
            return null;
        }
    }

    // 線上 → REST と同じ行（欠落は null のまま。解釈が応答全体を読めないへ倒す）。
    internal static HttpOpeningInventorySource.OpeningInventoryDto ToRow(Proto.OpeningInventoryLot l) => new(
        l.HasSymbol ? l.Symbol : null,
        RiskManagementWire.Market(l.Market),
        RiskManagementWire.Side(l.Side),
        l.HasQuantity ? l.Quantity : null,
        RiskManagementWire.Decimal(l.HasAverageCostInBase, l.AverageCostInBase),
        RiskManagementWire.Decimal(l.HasAverageFxRateBaseToDisplay, l.AverageFxRateBaseToDisplay),
        l.HasUnrecordedFxRateFillCount ? l.UnrecordedFxRateFillCount : null);

    private static Proto.Market ToProto(Market market) => market switch
    {
        Market.Japan => Proto.Market.Japan,
        Market.UnitedStates => Proto.Market.UnitedStates,
        _ => Proto.Market.Unspecified,
    };
}
