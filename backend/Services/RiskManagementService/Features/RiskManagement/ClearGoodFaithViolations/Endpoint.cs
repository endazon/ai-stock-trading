using AiStockTrading.Shared.Contracts.Events;
using Wolverine;

namespace RiskManagementService.Features.RiskManagement.ClearGoodFaithViolations;

// ---- GFV 違反による停止の解除（FR-19, FR-10, FR-11, UC-06, #464, ADR-0028 決定2/決定3, IADR-0182）----
//
// **OwnerOnly。** ADR-0028 §結果 が「解除操作そのものが新たな攻撃面・誤操作面になる」と明記しており、
// 既存の破壊的統制操作（kill switch・pause）と同じ権限管理の下に置く。s2s トークンでは 403
// （生成AI・自動処理が統制を解けないようにする）。
//
// 🔴 **解除は記録を消さない**（決定1「違反記録は失効させない」）。解除行を追記するだけであり、
// 違反記録そのものは台帳に残る。
//
// 🔴 **未供給時の fail-closed は解けない。** 決定2 の解除は**記録が積まれた場合**の解除であり、
// 供給が無い場合の拒否を解除する手段ではない（本経路は件数にしか作用しない）。
//
// **解除の窓口は Discord Bot である**（決定3）。本エンドポイントはその実行先であり、
// 画面（SC-02 / SC-03）からは呼ばない（BFF にプロキシ経路を作らない）。
internal static class ClearGoodFaithViolationsEndpoint
{
    public static void MapClearGoodFaithViolations(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/good-faith-violations/clear",
            async (GoodFaithViolationClearRequest req, GoodFaithViolationClearingService svc,
                   IMessageBus bus, HttpContext http) =>
        {
            var outcome = svc.Clear(RiskControlEndpoints.ActorOf(http), req.Reason ?? string.Empty);

            if (!outcome.Accepted)
            {
                var error = DescribeGfvClearingRejection(outcome.Rejection);
                return outcome.Rejection == GoodFaithViolationClearingRejection.NothingToClear
                    ? Results.UnprocessableEntity(new { error })
                    : Results.BadRequest(new { error });
            }

            // FR-11, ADR-0028 決定2: **誰が・いつ・どの記録に対して**解除したかを中央監査集約へ発行する。
            // 永続化（解除台帳）はサービス内で先に完了しており、それが権威（fail-safe・IADR-0082 と同型）。
            await bus.PublishAsync(new GoodFaithViolationsCleared(
                RiskControlEndpoints.ActorOf(http), req.Reason!, outcome.ClearedOrderIds, outcome.RemainingCount,
                outcome.ClearedAt!.Value));

            return Results.Ok(new
            {
                clearedOrderIds = outcome.ClearedOrderIds,
                clearedAt = outcome.ClearedAt,
                // **0 とは限らない。**「解除したのに止まったまま」を利用者応答からも説明できるようにする。
                remainingCount = outcome.RemainingCount,
            });
        });

    // #464, ADR-0028, IADR-0182: GFV 解除を受理しない理由を利用者向け文言に写す。
    // **何が足りないかを具体的に返す**——「不正な要求です」では、理由が必須であること自体が伝わらない。
    private static string DescribeGfvClearingRejection(GoodFaithViolationClearingRejection rejection) => rejection switch
    {
        GoodFaithViolationClearingRejection.ReasonRequired =>
            "reason（解除の理由）は必須です。原因の是正が済んでいることを記録として残してください。",
        GoodFaithViolationClearingRejection.ActorRequired => "解除者を特定できませんでした。",
        GoodFaithViolationClearingRejection.NothingToClear =>
            "解除できる GFV 違反記録がありません（停止していません）。"
            + "**発生回数が未供給であることによる拒否は、本操作では解除できません**（ADR-0028）。",
        _ => "解除要求を受理できません。",
    };
}

// #464, ADR-0028 決定2, IADR-0182: GFV 違反による停止の解除要求。
// **理由は必須**（`string?` で受けて空を明示的に弾く——非 nullable で受けると本文省略時に空文字へ
// 暗黙束縛され、「理由なしの解除」が通る）。**確認フレーズは Discord 側の閂であり本文には含めない**
// （kill switch と同型。窓口は Discord Bot＝決定3）。
internal sealed record GoodFaithViolationClearRequest(string? Reason);
