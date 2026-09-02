using AiStockTrading.Shared.Contracts.Trading;
using Wolverine;

namespace RiskManagementService.Features.RiskManagement.ClosePosition;

// ---- 建玉の手仕舞い（FR-10, FR-11, UC-06, #292, IADR-0117）: 利用者のみ（OwnerOnly）。理由必須 ----
// FR-10 本文「kill switch・日次損失ロックアウト・一時停止は…いずれも手仕舞い（Close）と損切りは止めない」に従い、
// 発注前スクリーニング（OrderScreeningService）を通さない（手仕舞い・損切りは統制で止めない〔FR-10〕・旧 IADR-0015 → IADR-0210 決定3 の規律）。
// サービストークンには開かない（生成AI・自動処理が建玉を落とせないようにする＝FR-10「生成AIはこれらを上書きできない」・ADR-0003）。
internal static class ClosePositionEndpoint
{
    public static void MapClosePosition(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/positions/close",
            async (PositionCloseRequest req, PositionCloseService svc, IMessageBus bus, HttpContext http) =>
        {
            // market は Market?（非 nullable enum は省略時に暗黙 0＝日本市場へ束縛され、誤市場の決済を通してしまう）。
            if (string.IsNullOrWhiteSpace(req.Symbol))
                return Results.BadRequest(new { error = "symbol（銘柄コード）は必須です。" });
            if (req.Market is not { } market || !Enum.IsDefined(market))
                return Results.BadRequest(new { error = "market は有効な市場（Japan/UnitedStates）を指定してください。" });
            if (string.IsNullOrWhiteSpace(req.Reason))
                return Results.BadRequest(new { error = "reason（理由）は必須です。" });

            var outcome = svc.Request(
                new PositionCloseCommand(req.Symbol, market, req.Quantity, req.LimitPrice, req.Reason),
                RiskControlEndpoints.ActorOf(http));

            if (!outcome.Accepted)
            {
                var error = DescribeRejection(outcome.Rejection);
                return outcome.Rejection == PositionCloseRejection.PositionNotFound
                    ? Results.NotFound(new { error })
                    : Results.UnprocessableEntity(new { error });
            }

            // FR-11: 監査（誰が・なぜ）を先に発行する。OrderApproved はアクターも理由も持たないため、これが無いと
            // 手仕舞いの操作者が監査台帳に残らない。順序の根拠は「起きた操作に監査が無い」より「監査があるのに操作が
            // 無い」ほうが安全（後者は同一 DecisionId の後続イベント不在で検知できる）。
            await bus.PublishAsync(outcome.Requested!);
            await bus.PublishAsync(outcome.Approval!);

            // 約定は後から非同期に成立するため 202（200 ではない）。以降は既存経路（発注執行 → OrderExecuted → 台帳 → 通知）。
            var intent = outcome.Approval!.Intent;
            return Results.Accepted(value: new
            {
                decisionId = outcome.Approval.DecisionId,
                symbol = intent.Symbol,
                market = intent.Market,
                side = intent.Side,
                quantity = intent.Quantity,
                price = intent.Price,
                mode = intent.Mode,
            });
        });

    // #292, IADR-0117: 決済要求の拒否理由を利用者向け文言に写す（HTTP ステータスは呼び出し側で分ける）。
    private static string DescribeRejection(PositionCloseRejection rejection) => rejection switch
    {
        PositionCloseRejection.PositionNotFound => "該当する建玉がありません（全決済済みを含む）。",
        PositionCloseRejection.InvalidQuantity => "quantity は 1 以上を指定してください。",
        PositionCloseRejection.ExceedsAvailable =>
            "決済数量が利用可能数量（建玉 − 処理中の決済）を超えています。処理中の決済の約定を待って再試行してください。",
        PositionCloseRejection.PriceUnavailable =>
            "決済価格を決められません（現在値を取得できず limitPrice も指定されていません）。limitPrice を指定してください。",
        _ => "決済要求を受理できません。",
    };
}

// #292, IADR-0117: 建玉の手仕舞い要求（理由必須）。売買方向は含めない（建玉方向からサーバが決める）。
// Market は nullable。非 nullable enum は本文省略時に既定値 0（＝Japan）へ暗黙束縛され、意図しない市場の建玉を
// 対象にしてしまうため、省略を 400 として弾けるようにする。
// Quantity 省略＝処理中を除いた残り全量。LimitPrice 省略＝現在値。
internal sealed record PositionCloseRequest(
    string? Symbol,
    Market? Market,
    int? Quantity,
    decimal? LimitPrice,
    string? Reason);
