using AiStockTrading.Shared.Contracts.Trading;
using RiskManagementService.Domain;

namespace RiskManagementService.Features.RiskManagement.UpdateStopLossMethod;

// ---- 損切りの実行機構（FR-10, FR-12, SC-02, UC-06, ADR-0040 決定1・決定3, #819, IADR-0342 決定2）----
// moomoo SIMULATE に限り損切りの実行機構を S0（既定・ブローカー側逆指値）/ S1 / S2 / S3 から選べる。
// **変更は利用者のみ**（OwnerOnly。生成 AI・サービス間呼び出しは変更できない）・**理由必須**・前後値つきで履歴に残す。
// **発注先が実弾（moomoo REAL）の間は S0 以外を選べない**（400・設定も履歴も変えない）。
// 現在値は `GET /settings` の `stopLossMethod`（SC-02 / SC-03 の読み取り）。画面からの変更は BFF
// `PUT /bff/risk-controls/settings/stop-loss-method` 経由（#823, IADR-0422 決定1・決定2）。
internal static class UpdateStopLossMethodEndpoint
{
    public static void MapUpdateStopLossMethod(this IEndpointRouteBuilder owner) =>
        owner.MapPut("/settings/stop-loss-method",
            (StopLossMethodUpdateRequest req, RiskSettingsService svc, HttpContext http) =>
        {
            // 省略（null）は 400。非 nullable enum で受けると本文省略時に既定値 0（＝S0）へ暗黙束縛され、
            // 「送っていない値へ黙って切り替わる」経路になる（BrokerProviderUpdateRequest.Provider と同じ規律）。
            if (req.Method is not { } target)
            {
                return Results.BadRequest(new
                {
                    error = "method は損切りの実行機構（0=S0 ブローカー側逆指値 / 1=S1 / 2=S2 逆指値なしの建玉を許容 / 3=S3）を指定してください。",
                });
            }

            var rejections = svc.UpdateStopLossMethod(target, RiskControlEndpoints.ActorOf(http), req.Reason);
            if (rejections.Count > 0)
            {
                return Results.BadRequest(new
                {
                    error = "損切りの実行機構の変更を受理できません。",
                    details = rejections.Select(Describe).ToArray(),
                });
            }

            return Results.Ok(svc.GetCurrent());
        });

    // 何が足りないかを具体的に返す（「不正な要求です」だけでは対処が分からない。発注先の変更と同じ流儀）。
    private static string Describe(StopLossMethodChangeRejection rejection) => rejection switch
    {
        StopLossMethodChangeRejection.ReasonRequired =>
            "変更理由は 1 文字以上を指定してください（監査のため必須）。",
        StopLossMethodChangeRejection.UnknownMethod =>
            "method は 0=S0 / 1=S1 / 2=S2 / 3=S3 のいずれかを指定してください。",
        StopLossMethodChangeRejection.NotPermittedOnLive =>
            "発注先が実弾（moomoo REAL）の間は、損切りの実行機構を S0（ブローカー側逆指値）以外にできません。"
            + "S1〜S3 は moomoo SIMULATE でのみ選べます。",
        _ => "損切りの実行機構の変更を受理できません。",
    };
}

// FR-10, SC-02, #819: 損切りの実行機構の変更要求（理由必須・FR-11）。
// Method は nullable（省略をエンドポイントで 400 に弾く。既定値 0 への暗黙束縛を防ぐ）。
internal sealed record StopLossMethodUpdateRequest(StopLossExecutionMethod? Method, string? Reason);
