using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision;
using Proto = AiStockTrading.Shared.Grpc.OrderExecution.V1;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// 🔴 NFR, FR-04, FR-10, MSP:ADR-0029, IADR-0284 決定 5（段 5 の追補 2）, IADR-0535 決定2・3, IADR-0559 決定 3・4, #1391 (#753):
// 処理中の決済が建玉を覆うかを **gRPC 生成クライアント**（`OrderExecutionRead/GetInFlightCloses`）で読む `IInFlightCloseProvider` の
// 2 つ目の実装。REST 実装（HttpInFlightCloseProvider）と並走する（**既定は REST**。`OrderExecution:Grpc` を宣言したときだけ選ばれる）。
//
// 🔴 **応答の解釈は REST と同じ 1 つ**（`HttpInFlightCloseProvider.Interpret`）。gRPC は線上の応答を同じ nullable の DTO へ写すだけ
// （銘柄・株数の欠落は null、市場・方向・状態の未指定・未知は null ＝不明）。
// 🔴 **どの失敗も null（不明）へ倒す**。不明のとき判断は LLM を呼ぶ（見送らない）。「覆う」を読み違えて見送る向きの誤りを作らない（IADR-0535 決定3）。
public sealed class GrpcInFlightCloseProvider(
    OrderExecutionGrpcTransport transport,
    ILogger<GrpcInFlightCloseProvider> logger)
    : IInFlightCloseProvider
{
    public async Task<InFlightCloseCoverage?> GetAsync(
        string symbol, Market market, TradeSide closeSide, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        var response = await transport.CallAsync(
            "処理中の決済（in-flight-closes）",
            "不明として扱います（LLM を呼びます）。",
            (client, options) => client.GetInFlightClosesAsync(
                new Proto.GetInFlightClosesRequest
                {
                    Symbol = symbol,
                    Market = OrderExecutionWire.ToProto(market),
                    CloseSide = OrderExecutionWire.ToProto(closeSide),
                },
                options),
            cancellationToken).ConfigureAwait(false);

        return response is null
            ? null
            : HttpInFlightCloseProvider.Interpret(ToDto(response), symbol, market, closeSide, logger);
    }

    // ClosableQuantity・DecisionIds は判定に使わない（REST の受け手も読まない）ため線上に無い＝null。
    internal static HttpInFlightCloseProvider.InFlightClosesDto ToDto(Proto.GetInFlightClosesResponse response) =>
        new(
            response.HasSymbol ? response.Symbol : null,
            OrderExecutionWire.Market(response.Market),
            OrderExecutionWire.Side(response.CloseSide),
            OrderExecutionWire.Status(response.Status),
            response.HasUnknownReason ? response.UnknownReason : null,
            ClosableQuantity: null,
            response.HasInFlightQuantity ? response.InFlightQuantity : null,
            [.. response.OrderIds.Select(id => (string?)id)],
            DecisionIds: null);
}
