using AiStockTrading.Shared.Contracts.Trading;
using Wolverine;

namespace RiskManagementService.Features.RiskManagement.AdoptPositionDrift;

// ---- 乖離の取り込み（FR-10, FR-11, UC-06, ADR-0003, #849, IADR-0350）: 利用者のみ（OwnerOnly）。理由必須 ----
//
// システム外の売買（証券会社のアプリからの直接の売却など）で台帳とブローカーが乖離したとき、利用者が内容を確認して
// **観測されているブローカーの建玉へ台帳の数量を合わせる**。乖離の検知（IADR-0118）は是正しないため、
// これが無いと実在しない建玉が段階資金・保有建玉数の枠を占有し続け、新規建てが止まる（#849 の実測）。
//
// サービストークンには開かない（生成AI・自動処理が台帳を書き換えられないようにする＝FR-10「生成AIはこれらを
// 上書きできない」・ADR-0003。手仕舞い・kill switch と同じ権限管理）。
// **数量は受け取らない**——利用者が任意の数量で台帳を書き換える API にしない。
internal static class AdoptPositionDriftEndpoint
{
    public static void MapAdoptPositionDrift(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/position-drift/adopt",
            async (PositionDriftAdoptionRequest req, PositionDriftAdoptionService svc, IMessageBus bus, HttpContext http) =>
        {
            // market は Market?（非 nullable enum は省略時に暗黙 0＝日本市場へ束縛され、誤市場の建玉を対象にしてしまう）。
            if (string.IsNullOrWhiteSpace(req.Symbol))
                return Results.BadRequest(new { error = "symbol（銘柄コード）は必須です。" });
            if (req.Market is not { } market || !Enum.IsDefined(market))
                return Results.BadRequest(new { error = "market は有効な市場（Japan/UnitedStates）を指定してください。" });
            if (string.IsNullOrWhiteSpace(req.Reason))
            {
                return Results.BadRequest(new
                {
                    error = "reason（理由）は必須です。何が起きて台帳を合わせるのかを記録として残してください。",
                });
            }

            var outcome = svc.Adopt(
                new PositionDriftAdoptionCommand(req.Symbol, market, req.Reason),
                RiskControlEndpoints.ActorOf(http));

            if (!outcome.Accepted)
            {
                return Results.UnprocessableEntity(new
                {
                    error = DescribeRejection(outcome.Rejection),
                    code = outcome.Rejection.ToString(),
                });
            }

            // FR-11: 取り込み行（台帳）は既に永続しており、それが権威（fail-safe・IADR-0082 と同型）。
            // ここでは誰が・なぜ・何を観測して・どう変えたかを中央監査台帳と通知へ流す。
            var adopted = outcome.Adopted!;
            await bus.PublishAsync(adopted);

            return Results.Ok(new
            {
                adoptionId = adopted.AdoptionId,
                symbol = adopted.Symbol,
                market = adopted.Market,
                ledgerQuantityBefore = adopted.LedgerQuantityBefore,
                ledgerQuantityAfter = adopted.LedgerQuantityAfter,
                brokerQuantity = adopted.BrokerQuantity,
                observedAt = adopted.ObservedAt,
                // **実現損益は記録していない**（不明）。推定は参考であり、台帳のどの数値にも入っていない。
                realizedPnlRecorded = adopted.RealizedPnlRecorded,
                referencePrice = adopted.ReferencePrice,
                estimatedPnlInBase = adopted.EstimatedPnlInBase,
                adoptedAt = adopted.AdoptedAt,
            });
        });

    // **何が足りないか・どうすれば通るかを具体的に返す**（「受理できません」では次の一手が分からない）。
    private static string DescribeRejection(PositionDriftAdoptionRejection rejection) => rejection switch
    {
        PositionDriftAdoptionRejection.ReasonRequired => "reason（理由）は必須です。",
        PositionDriftAdoptionRejection.ObservationUnavailable =>
            "ブローカ建玉の観測がまだ届いていません（照会不能を含む）。観測できていない値へ台帳を合わせることはできません。",
        PositionDriftAdoptionRejection.ObservationStale =>
            "ブローカ建玉の最新の観測が古すぎます（60 分超）。建玉の照会が止まっていないか確認し、次の観測を待って再試行してください。",
        PositionDriftAdoptionRejection.NoDrift =>
            "当該銘柄に取り込む乖離がありません（最新の観測と台帳は一致しています。取り込み済みを含む）。",
        PositionDriftAdoptionRejection.DriftNotReported =>
            "その乖離はまだ報告されていません（同じ内容を連続して観測したときに報告されます）。"
            + "約定が台帳へ届く前の一過性のずれかもしれないため、乖離の通知を待って再試行してください。",
        PositionDriftAdoptionRejection.UnsupportedDirection =>
            "取り込めるのは台帳の建玉を減らす乖離だけです。台帳に無い建玉・数量の増加・方向の反転は、"
            + "取得単価も損切りも分からない建玉を台帳へ作ることになるため取り込みません。",
        PositionDriftAdoptionRejection.LedgerMovedAfterObservation =>
            "最新の観測より後に台帳の当該銘柄が動いています。次の観測を待って再試行してください。",
        PositionDriftAdoptionRejection.CloseInFlight =>
            "当該銘柄に処理中の決済（承認済み・未約定）があります。約定が後から届くと二重に減るため、"
            + "決済の約定または 30 分の経過を待って再試行してください。",
        _ => "取り込み要求を受理できません。",
    };
}

// #849, IADR-0350: 乖離の取り込み要求（理由必須）。**数量は含めない**（観測から決まる）。
// Market は nullable。非 nullable enum は本文省略時に既定値 0（＝Japan）へ暗黙束縛されるため、省略を 400 として弾く。
internal sealed record PositionDriftAdoptionRequest(string? Symbol, Market? Market, string? Reason);
