using InformationCollectionService.Features.InformationCollection;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.CostControl.V1;

namespace InformationCollectionService.Infrastructure.ExternalServices;

// NFR（費用）, FR-01, MSP:ADR-0029, IADR-0284 決定 5（段 4）, IADR-0031, IADR-0446, #1061 (#753):
// 費用統制の判定を **gRPC 生成クライアント**（`CostStateRead/GetCostState`）で照会する `ICostControlGate` の 2 つ目の実装。
// REST 実装（HttpCostControlGate）と並走する（**既定は REST**。`CostControl:Grpc` を宣言したときだけ選ばれる）。
//
// 🔴 **写しは REST と同じ 1 つ**（`HttpCostControlGate.Map`。#915 の是正: 停止の欠落は Normal、停止は倍率を見ずに尊重、倍率の欠落・非正は 1×）。
// 🔴 **倒す向きは REST と同じ Normal（停止せず・1×）**: 照会の失敗。読めない倍率は「倍率なし」（停止は守る。#1063 B）。
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

        // #1063 B: 倍率が読めない（書式・桁あふれ）ときは倍率を「無い」として Map へ渡す —— 停止の旗が読めていれば停止を守り、
        // 停止していない応答なら Normal（1×）になる（REST と同じ。以前は応答全体を Normal へ倒し、停止中でも収集を続けた）。
        var multiplier = CostControlWire.IntervalMultiplier(response, out var unreadable);
        if (unreadable)
            logger.LogWarning("費用統制の gRPC 応答の倍率を読めません。倍率は無いものとして扱います（停止の旗は守ります）。");

        return HttpCostControlGate.Map(CostControlWire.IsHalted(response), multiplier, logger);
    }
}
