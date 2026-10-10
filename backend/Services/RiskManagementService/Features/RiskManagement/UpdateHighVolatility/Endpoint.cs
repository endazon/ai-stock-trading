using RiskManagementService.Domain;

namespace RiskManagementService.Features.RiskManagement.UpdateHighVolatility;

// ---- 高ボラティリティ銘柄の統制値（FR-10, UC-06, ADR-0063 決定1・決定2, #1291, IADR-0527 決定2）----
// 区分の 1 注文あたりの発注金額上限（equity 比。既定 0.05）と、利用者の明示指定（銘柄・市場の一覧）を置き換える。
// **変更は利用者のみ**（OwnerOnly。生成 AI・サービス間呼び出し・AI の監視銘柄の入れ替え案〔ADR-0042〕は変更できない）・**理由必須**・
// 前後値つきで履歴に残す（SettingsChangeType.HighVolatilityChanged）。値域外は 400（設定も履歴も変えない）。
// 現在値は `GET /settings` の `highVolatility`。🔴 画面（SC-02）からの変更と BFF の経路はまだ無い（ADR-0063 フォローアップ 5・人間）。
internal static class UpdateHighVolatilityEndpoint
{
    public static void MapUpdateHighVolatility(this IEndpointRouteBuilder owner) =>
        owner.MapPut("/settings/high-volatility",
            (HighVolatilityUpdateRequest req, RiskSettingsService svc, HttpContext http) =>
        {
            // 省略（null）は 400。既定値への暗黙束縛で「送っていない値」へ黙って切り替えない（損切りの実行機構の変更と同じ規律）。
            if (req.MaxOrderAmountRatio is not { } ratio || req.DesignatedSymbols is not { } symbols)
            {
                return Results.BadRequest(new
                {
                    error = "maxOrderAmountRatio（equity 比）と designatedSymbols（明示指定の銘柄の一覧。無ければ空の一覧）を指定してください。",
                });
            }

            var settings = new HighVolatilitySettings { MaxOrderAmountRatio = ratio, DesignatedSymbols = symbols };
            var violations = HighVolatilityOrderCap.Validate(settings);
            if (violations.Count > 0)
            {
                // 拒否時は設定を変更せず履歴も残さない（`UpdateHighVolatility` を呼ばない）。
                return Results.BadRequest(new
                {
                    error = "高ボラティリティ銘柄の設定が受理できません。",
                    details = violations,
                });
            }

            svc.UpdateHighVolatility(settings, RiskControlEndpoints.ActorOf(http), req.Reason ?? string.Empty);
            return Results.Ok(svc.GetCurrent());
        });
}

// FR-10, UC-06, #1291: 高ボラティリティ銘柄の統制値の変更要求（理由必須・FR-11）。各項目は nullable（省略をエンドポイントで 400 に弾く）。
internal sealed record HighVolatilityUpdateRequest(
    decimal? MaxOrderAmountRatio, List<HighVolatilitySymbol>? DesignatedSymbols, string? Reason);
