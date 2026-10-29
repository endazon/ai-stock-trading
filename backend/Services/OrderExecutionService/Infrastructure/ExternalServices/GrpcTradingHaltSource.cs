using Microsoft.Extensions.Logging;
using OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace OrderExecutionService.Infrastructure.ExternalServices;

// 🔴 NFR, FR-10, ADR-0009, MSP:ADR-0029, IADR-0284 決定 5（段 5 の追補 3）, IADR-0567 決定 2, IADR-0604, #1510 (#753):
// 停止の状態（kill switch・一時停止）を **gRPC 生成クライアント**（`RiskControlsRead/GetTradingHalt`）で照会する `ITradingHaltSource` の
// 2 つ目の実装。REST 実装（HttpTradingHaltSource）と並走する（**既定は REST**。`RiskManagement:Grpc` を宣言したときだけ選ばれる）。
//
// 🔴 **応答の解釈は REST と同じ 1 つ**（`HttpTradingHaltSource.Interpret`。項目の欠落は不明＝新規建てを送らない）。
// 🔴 **どの失敗も null（不明）へ倒す**。呼び出し元の取り消しでも投げずに不明を返す（REST と同じ。予約を取った後に呼ばれるため。IADR-0567 決定 4）。
public sealed class GrpcTradingHaltSource(
    RiskManagementGrpcTransport transport,
    ILogger<GrpcTradingHaltSource> logger)
    : ITradingHaltSource
{
    public async Task<TradingHaltState?> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await transport.CallAsync(
                "停止の状態（kill switch・一時停止）",
                "不明として扱います（新規建てを送りません）。",
                (client, options) => client.GetTradingHaltAsync(new Proto.GetTradingHaltRequest(), options),
                cancellationToken).ConfigureAwait(false);

            return response is null ? null : HttpTradingHaltSource.Interpret(ToDto(response), logger);
        }
        catch (Exception ex)
        {
            // 呼び出し元の取り消しを含む（REST と同じ）。
            logger.LogWarning(ex, "停止の状態（kill switch・一時停止）の gRPC 照会で例外が発生しました。不明として扱います（新規建てを送りません）。");
            return null;
        }
    }

    /// <summary>線上 → REST と同じ nullable の DTO（欠落は null ＝不明）。</summary>
    internal static HttpTradingHaltSource.TradingHaltDto ToDto(Proto.GetTradingHaltResponse response) =>
        new(
            response.HasKillSwitchEngaged ? response.KillSwitchEngaged : null,
            response.HasTradingPaused ? response.TradingPaused : null);
}
