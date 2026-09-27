using System.Globalization;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using CostControlService.Domain;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Proto = AiStockTrading.Shared.Grpc.CostControl.V1;

namespace CostControlService.Features.CostControl;

// NFR（費用）, FR-01, MSP:ADR-0029, MSP:ADR-0075, IADR-0284 決定 5（段 4）, IADR-0328, IADR-0331, IADR-0446 決定 2,
// #1061 (#753):
// 費用統制の現在の判定の gRPC 面。REST の `GET /costs/state`（GetCostStateEndpoint）と**同じ**サービス
// （CostControlAppService.GetLlmStateAsync）を呼ぶ —— 評価器を 2 つにしない。
//
// 認可: REST の read サブグループと同じ `OwnerOrService` に、所有者の分岐だけ呼び出し元のクライアント（`azp`）の確認を足した `GrpcOwnerOrService`（#1067。下の属性）（IADR-0051）。s2s トークンが無ければ `UNAUTHENTICATED`、
// ロールが無ければ `PERMISSION_DENIED`。
//
// 🔴 **並走中の正は REST である**（MSP:ADR-0029 の 2026-08-04 追記）。REST 面は変えていない。
// 🔴 NFR-06, ADR-0047 決定 3, IADR-0448, #1067: 門は **`GrpcOwnerOrService`**（REST の `OwnerOrService` ではない）。
// s2s（trading-service）は同じ、所有者（trading-owner）はトークンの `azp` が Discord ボットの機密クライアントであるときだけ通す
// ＝人の利用者のトークンは gRPC 面を通らない（REST の面の判定は変えていない）。
[Authorize(Policy = AiStockTradingAuthPolicies.GrpcOwnerOrService)]
public sealed class CostStateReadGrpcService(CostControlAppService costs) : Proto.CostStateRead.CostStateReadBase
{
    public override async Task<Proto.GetCostStateResponse> GetCostState(
        Proto.GetCostStateRequest request, ServerCallContext context) =>
        CostStateWireMapping.ToProto(await costs.GetLlmStateAsync(context.CancellationToken).ConfigureAwait(false));
}

// NFR, IADR-0446 決定 3: 送り手の型 → 線上表現（提供側の写し）。停止の有無と倍率（不変文化の 10 進）だけを運ぶ。
// 🔴 在る false・在る 0 は**設定する**（停止していない・Halted の倍率 0 は値であり、欠落とは違う）。
public static class CostStateWireMapping
{
    public static Proto.GetCostStateResponse ToProto(CostControlDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return new Proto.GetCostStateResponse
        {
            IsHalted = decision.IsHalted,
            IntervalMultiplier = decision.IntervalMultiplier.ToString(CultureInfo.InvariantCulture),
        };
    }
}
