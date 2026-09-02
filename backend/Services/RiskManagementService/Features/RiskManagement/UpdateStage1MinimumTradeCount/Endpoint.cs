using RiskManagementService.Domain;

namespace RiskManagementService.Features.RiskManagement.UpdateStage1MinimumTradeCount;

// ---- Stage 1 の最小取引件数（FR-20, FR-13, SC-02, #423, 06_daytrading-review §4.1 条件 3, IADR-0164）----
// 2026-08-07 の裁定により、条件 3 の件数は SC-02 から変更できる設定値になった（既定 100・値域 1〜1000）。
// **100 件未満でも受理する**——裁定は「警告は設定を妨げない。下げた事実が記録に残ることを担保する」
// と定めている。警告は `Stage1GateCriteria.BelowStatisticalBasis`（GET /stage-gate）が宣言する。
// **ここで拒否すると裁定に反する**（利用者が下げられなくなる）。
internal static class UpdateStage1MinimumTradeCountEndpoint
{
    public static void MapUpdateStage1MinimumTradeCount(this IEndpointRouteBuilder owner) =>
        owner.MapPut("/settings/stage1-minimum-trade-count",
            (Stage1MinimumTradeCountUpdateRequest req, RiskSettingsService svc, HttpContext http) =>
        {
            // 省略（null）は 400。非 nullable int で受けると本文省略時に既定値 0 へ暗黙束縛され、
            // 「送っていない値へ黙って切り替わる」経路になる（BrokerProviderUpdateRequest.Provider と同じ規律）。
            // 0 は値域外のため実害は無いが、400 の文言を「省略」と「値域外」で分けられるようにする。
            if (req.MinimumTradeCount is not { } value)
            {
                return Results.BadRequest(new { error = "minimumTradeCount（件数）は必須です。" });
            }

            if (Stage1TradeCountBounds.Validate(value) is { } violation)
            {
                // 拒否時は設定を変更せず履歴も残さない（`UpdateStage1MinimumTradeCount` を呼ばない）。
                return Results.BadRequest(new
                {
                    error = "Stage 1 の最小取引件数が設定可能な範囲を外れています。",
                    details = new[] { violation },
                });
            }

            svc.UpdateStage1MinimumTradeCount(value, RiskControlEndpoints.ActorOf(http), req.Reason ?? string.Empty);
            return Results.Ok(svc.GetCurrent());
        });
}

// FR-20, FR-13, SC-02, #423: Stage 1 の最小取引件数の変更要求（理由必須・FR-11）。
// MinimumTradeCount は nullable（省略をエンドポイントで 400 に弾く。既定値 0 への暗黙束縛を防ぐ）。
internal sealed record Stage1MinimumTradeCountUpdateRequest(int? MinimumTradeCount, string? Reason);
