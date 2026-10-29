using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision;
using Proto = AiStockTrading.Shared.Grpc.CostControl.V1;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// 🔴 NFR-13, ADR-0074 決定2, MSP:ADR-0029, IADR-0284 決定 5（段 5 の追補 3）, IADR-0582, IADR-0604, #1510 (#753):
// 月次 LLM 費用の停止を **gRPC 生成クライアント**（`CostStateRead/GetCostState`）で照会する `ILlmCostHaltGate` の 2 つ目の実装。
// REST 実装（HttpLlmCostHaltGate）と並走する（**既定は REST**。`CostControl:Grpc` を宣言したときだけ選ばれる）。
//
// 🔴 **倒す向きは REST と同じ**: 停止しているのは `is_halted` が明示的に true のときだけ。照会の失敗・項目の欠落は「停止していない」
// （判断は LLM を呼ぶ＝従来どおり。費用の統制で資金の保護を止めない向き。IADR-0031 / IADR-0582 と同じ判断）。
public sealed class GrpcLlmCostHaltGate(CostControlGrpcTransport transport, ILogger<GrpcLlmCostHaltGate> logger) : ILlmCostHaltGate
{
    public async Task<bool> IsHaltedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await transport.CallAsync(
                "費用統制",
                "停止していないものとして判断を続けます。",
                (client, options) => client.GetCostStateAsync(new Proto.GetCostStateRequest(), options),
                cancellationToken).ConfigureAwait(false);
            return Interpret(response, logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // REST（HttpLlmCostHaltGate）と同じ向き: 呼び出し元自身の取り消しだけを伝播し、他の例外（破棄済みのチャネル等）は停止していない。
            logger.LogWarning(ex, "費用統制の gRPC 照会で例外が発生しました。停止していないものとして判断を続けます。");
            return false;
        }
    }

    /// <summary>線上の応答 → 停止しているか（純関数に近い）。応答なし・欠落は false（停止していない）。</summary>
    internal static bool Interpret(Proto.GetCostStateResponse? response, ILogger logger)
    {
        if (response is null)
            return false;
        if (response.HasIsHalted)
            return response.IsHalted;

        logger.LogWarning("費用統制の gRPC 応答に is_halted を読めません。停止していないものとして判断を続けます。");
        return false;
    }
}
