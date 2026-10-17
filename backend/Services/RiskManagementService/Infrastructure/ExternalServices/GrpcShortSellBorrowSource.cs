using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using RiskManagementService.Features.RiskManagement;
using Proto = AiStockTrading.Shared.Grpc.OrderExecution.V1;

namespace RiskManagementService.Infrastructure.ExternalServices;

// NFR, FR-10, UC-06, ADR-0016 決定3, MSP:ADR-0029, IADR-0284 決定 5（段 5 の追補 2）, IADR-0425 決定4, IADR-0559 決定 3・4, #1391 (#753):
// 借株可否を **gRPC 生成クライアント**（`OrderExecutionRead/GetShortPermit`）で読む `IShortSellBorrowSource` の 2 つ目の実装。
// REST 実装（HttpShortSellBorrowSource）と並走する（**既定は REST**。`OrderExecution:Grpc` を宣言したときだけ選ばれる）。
//
// 🔴 **応答の解釈は REST と同じ 1 つ**（`HttpShortSellBorrowSource.Interpret`）。gRPC は線上の応答を同じ nullable の DTO へ写すだけ
// （銘柄の欠落は null、市場・状態の未指定・未知は null ＝契約の食い違い＝「分からない」）。
// 🔴 **どの失敗も「分からない」へ倒す**（許可へ倒す経路を持たない。REST と同じ）。審査は「分からない」なら空売り文脈を組まず拒否する。
public sealed class GrpcShortSellBorrowSource(
    OrderExecutionGrpcTransport transport,
    ILogger<GrpcShortSellBorrowSource> logger)
    : IShortSellBorrowSource
{
    public async Task<ShortSellBorrowObservation> GetAsync(
        string symbol, Market market, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        var response = await transport.CallAsync(
            "借株可否（short-permit）",
            "借株可否は不明として扱います。",
            (client, options) => client.GetShortPermitAsync(
                new Proto.GetShortPermitRequest { Symbol = symbol, Market = OrderExecutionWire.ToProto(market) }, options),
            cancellationToken).ConfigureAwait(false);

        // 照会の失敗（UNAVAILABLE・DEADLINE_EXCEEDED・UNAUTHENTICATED 等）は REST の非 2xx・例外・タイムアウトと同じ「分からない」。
        return response is null
            ? ShortSellBorrowObservation.Unknown("request-failed")
            : HttpShortSellBorrowSource.Interpret(ToDto(response), symbol, market, logger);
    }

    internal static HttpShortSellBorrowSource.ShortPermitDto ToDto(Proto.GetShortPermitResponse response) =>
        new(
            response.HasSymbol ? response.Symbol : null,
            OrderExecutionWire.Market(response.Market),
            OrderExecutionWire.Status(response.Status),
            response.HasUnknownReason ? response.UnknownReason : null);
}
