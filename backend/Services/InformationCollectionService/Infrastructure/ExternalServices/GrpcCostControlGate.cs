using InformationCollectionService.Features.InformationCollection;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.CostControl.V1;

namespace InformationCollectionService.Infrastructure.ExternalServices;

// NFR（費用）, FR-01, MSP:ADR-0029, IADR-0284 決定 5（段 4）, IADR-0031, IADR-0446, #1061 (#753):
// 費用統制の判定を **gRPC 生成クライアント**（`CostStateRead/GetCostState`）で照会する `ICostControlGate` の 2 つ目の実装。
// REST 実装（HttpCostControlGate）と並走する（**既定は REST**。`CostControl:Grpc` を宣言したときだけ選ばれる）。
//
// 🔴 **写しは REST と同じ 1 つ**（`HttpCostControlGate.Map`。#915 の是正: 停止の欠落は Normal、停止は倍率を見ずに尊重、倍率の欠落・非正は 1×）。
// 🔴 **倒す向きは REST と同じ Normal（停止せず・1×）**: 照会の失敗・読めない倍率。
public sealed class GrpcCostControlGate(CostControlGrpcTransport transport, ILogger<GrpcCostControlGate> logger) : ICostControlGate
{
    public async Task<CostControlGate> GetAsync(CancellationToken cancellationToken = default)
    {
        var response = await transport.CallAsync(
            "費用統制",
            "Normal（停止せず）に倒します。",
            (client, options) => client.GetCostStateAsync(new Proto.GetCostStateRequest(), options),
            cancellationToken).ConfigureAwait(false);
        if (response is null)
            return CostControlGate.Normal;

        try
        {
            return HttpCostControlGate.Map(
                CostControlWire.IsHalted(response), CostControlWire.IntervalMultiplier(response), logger);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            // 読めない書式・decimal の範囲を超える桁は、どちらも不正応答として Normal へ倒す（REST の不正応答と同じ向き）。
            logger.LogWarning(ex, "費用統制の gRPC 応答の倍率を読めません。Normal（停止せず）に倒します。");
            return CostControlGate.Normal;
        }
    }
}
