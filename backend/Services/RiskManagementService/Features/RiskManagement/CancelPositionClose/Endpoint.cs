using Wolverine;

namespace RiskManagementService.Features.RiskManagement.CancelPositionClose;

// ---- 板に残った手仕舞いの取消（FR-05, FR-10, FR-11, UC-06, #847, #768, IADR-0357）----
// 利用者のみ（OwnerOnly）・理由必須。サービストークンには開かない（生成AI・自動処理が注文を消せないように
// する＝FR-10「生成AIはこれらを上書きできない」・ADR-0003）。手仕舞い（/positions/close）と同じ形に揃える。
internal static class CancelPositionCloseEndpoint
{
    public static void MapCancelPositionClose(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/positions/close/cancel",
            async (PositionCloseCancelRequest req, PositionCloseCancellationService svc, IMessageBus bus,
                   HttpContext http) =>
        {
            if (req.DecisionId is not { } decisionId || decisionId == Guid.Empty)
                return Results.BadRequest(new { error = "decisionId（手仕舞いの判断ID）は必須です。" });
            if (string.IsNullOrWhiteSpace(req.Reason))
                return Results.BadRequest(new { error = "reason（理由）は必須です。" });

            var outcome = svc.Request(
                new PositionCloseCancellationCommand(decisionId, req.Reason),
                RiskControlEndpoints.ActorOf(http));

            if (!outcome.Accepted)
            {
                var error = DescribeRejection(outcome.Rejection);
                return outcome.Rejection == PositionCloseCancellationRejection.OrderNotFound
                    ? Results.NotFound(new { error })
                    : Results.UnprocessableEntity(new { error });
            }

            // FR-11: 「誰が・なぜ」を運ぶ本イベントが、監査台帳の証跡であり、同時に発注執行への駆動でもある
            //（OrderCancelled はアクターを持たない）。受け手は AuditService と OrderExecutionService の 2 つ。
            await bus.PublishAsync(outcome.Requested!);

            // 🔴 取消がブローカーへ届くのは非同期であり、**届いても取り消せたとは限らない**（moomoo は
            // 取消進行中を経て終端になり、その間に約定し得る）。したがって 200 ではなく 202 で返し、
            // 「取り消せた」とは言わない。確定は通知・監査（OrderCancelled / OrderExecuted）で届く。
            return Results.Accepted(value: new
            {
                decisionId = outcome.Requested!.DecisionId,
                symbol = outcome.Requested.Symbol,
                market = outcome.Requested.Market,
                accepted = true,
                note = "取消を要求しました。証券会社で取り消せたことが確認できるまで、この手仕舞いは"
                    + "「処理中の決済」として建玉を押さえ続けます（二重決済を防ぐため）。",
            });
        });

    private static string DescribeRejection(PositionCloseCancellationRejection rejection) => rejection switch
    {
        PositionCloseCancellationRejection.OrderNotFound =>
            "該当する手仕舞い注文がありません（判断IDが違うか、台帳に承認が届いていません）。",
        PositionCloseCancellationRejection.NotACloseOrder =>
            "手仕舞い以外の注文はこの口では取り消せません（エントリーの取消は発注執行が自ら行います）。",
        _ => "取消要求を受理できません。",
    };
}

// #847, IADR-0357: 取消要求。DecisionId は手仕舞いの応答（202）が返した値である。
// 非 nullable Guid は本文省略時に Guid.Empty へ暗黙束縛されるため nullable で受け、省略を 400 で弾く
//（ClosePosition の Market が nullable なのと同じ理由）。
internal sealed record PositionCloseCancelRequest(Guid? DecisionId, string? Reason);
