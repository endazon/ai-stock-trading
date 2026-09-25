using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// NFR, FR-04, FR-05, FR-10, MSP:ADR-0029, IADR-0284 決定 5（段 2）, IADR-0427, #997 (#753):
// 保有建玉・未約定の新規建て注文を **gRPC 生成クライアント**（`RiskControlsRead/GetOpenPositions`・
// `GetWorkingEntryOrders`）で照会する `IHeldPositionProvider` の 2 つ目の実装。REST 実装（HttpHeldPositionProvider）と並走し、
// 選ぶのは Program.cs（`RiskManagement:Grpc` の有無。**既定は REST**）。
//
// 🔴 **解釈は REST 実装と同じ 1 つを使う**（`HttpHeldPositionProvider.InterpretPositions` / `InterpretWorkingEntryOrders`。
// IADR-0427 決定 5）。ここは proto を **同じ nullable の行**へ写すだけで、行の検証（#943 / #934 の規則）を持たない。
// 🔴 **原則 A**: 欠落・未指定は null（不明）へ写す（RiskManagementWire）。0・列挙の 0（日本・買い）へ写すと、
// 識別できない行が「一致しない」＝「保有なし」へ黙って倒れる（#943 の実測そのもの）。
// 🔴 **fail-safe の区別も REST と同じ**: 取得できない・読めない（status・deadline・10 進／時刻の書式）は **null（不明）**、
// 一致する行が無ければ **None（保有なし／無い）**。
public sealed class GrpcHeldPositionProvider(
    RiskManagementGrpcTransport transport,
    ILogger<GrpcHeldPositionProvider> logger)
    : IHeldPositionProvider
{
    // #865, IADR-0358: 実結線（RiskManagement:Grpc が宣言されたときだけ生成される）。REST 実装と同じく常に true。
    public bool IsEnabled => true;

    public async Task<int?> GetSignedQuantityAsync(
        string symbol, Market market, CancellationToken cancellationToken = default) =>
        (await GetPositionAsync(symbol, market, cancellationToken).ConfigureAwait(false))?.SignedQuantity;

    public async Task<HeldPosition?> GetPositionAsync(
        string symbol, Market market, CancellationToken cancellationToken = default)
    {
        var response = await transport.CallAsync(
            "保有建玉",
            "不明として扱います。",
            (client, options) => client.GetOpenPositionsAsync(new Proto.GetOpenPositionsRequest(), options),
            cancellationToken).ConfigureAwait(false);
        if (response is null)
            return null;

        List<HttpHeldPositionProvider.OpenPositionDto> rows;
        try
        {
            rows = [.. response.Positions.Select(ToRow)];
        }
        catch (FormatException ex)
        {
            logger.LogWarning(ex, "保有建玉の gRPC 応答を読めません（10 進の書式）。不明として扱います。");
            return null;
        }

        return HttpHeldPositionProvider.InterpretPositions(rows, symbol, market, logger);
    }

    public async Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(
        string symbol, Market market, CancellationToken cancellationToken = default)
    {
        var response = await transport.CallAsync(
            "未約定の新規建て注文",
            "不明として扱います。",
            (client, options) => client.GetWorkingEntryOrdersAsync(new Proto.GetWorkingEntryOrdersRequest(), options),
            cancellationToken).ConfigureAwait(false);
        if (response is null)
            return null;

        List<HttpHeldPositionProvider.WorkingEntryOrderDto> rows;
        try
        {
            rows = [.. response.Orders.Select(ToRow)];
        }
        catch (FormatException ex)
        {
            logger.LogWarning(ex, "未約定の新規建て注文の gRPC 応答を読めません（10 進・時刻の書式）。不明として扱います。");
            return null;
        }

        return HttpHeldPositionProvider.InterpretWorkingEntryOrders(rows, symbol, market, logger);
    }

    // 線上 → REST と同じ nullable の行（欠落・未指定は null）。
    internal static HttpHeldPositionProvider.OpenPositionDto ToRow(Proto.OpenPositionRow p) => new(
        p.HasSymbol ? p.Symbol : null,
        RiskManagementWire.Market(p.Market),
        RiskManagementWire.Side(p.Side),
        p.HasQuantity ? p.Quantity : null,
        RiskManagementWire.Decimal(p.HasEntryPrice, p.EntryPrice),
        RiskManagementWire.Decimal(p.HasStopLossPrice, p.StopLossPrice));

    internal static HttpHeldPositionProvider.WorkingEntryOrderDto ToRow(Proto.WorkingEntryOrderRow o) => new(
        o.HasSymbol ? o.Symbol : null,
        RiskManagementWire.Market(o.Market),
        RiskManagementWire.Side(o.Side),
        o.HasRemainingQuantity ? o.RemainingQuantity : null,
        RiskManagementWire.Decimal(o.HasPrice, o.Price),
        RiskManagementWire.Timestamp(o.HasApprovedAt, o.ApprovedAt));
}
