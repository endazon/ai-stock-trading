namespace MarketMonitorService.Features.MarketMonitor.UpdateMovementThreshold;

// ---- 収集パラメータの部分更新（FR-03/FR-11/FR-13, UC-06, SC-01 §2, #340, IADR-0155）----
// 全置換 PUT（`ReplaceMonitorSettings`）は**全置換**であり、画面から使うと変動閾値だけを変えたい場面でも
// 監視銘柄を送り直す必要がある（**送り漏らした瞬間に監視銘柄が消える**）。SC-01 §2 が使う経路は項目単位の
// 部分更新とし、他の項目を巻き込まない。理由必須・値域検証（MonitorSettingsBounds）・履歴記録を伴う。
internal static class UpdateMovementThresholdEndpoint
{
    public static void MapUpdateMovementThreshold(this IEndpointRouteBuilder owner) =>
        owner.MapPut("/settings/movement-threshold",
            (MovementThresholdUpdateRequest req, MonitorSettingsService svc, HttpContext http) =>
        {
            // 非 nullable decimal で受けると本文省略時に既定値 0 へ暗黙束縛され、「送っていない値へ黙って
            // 切り替わる」経路になる（BrokerProviderUpdateRequest.Provider と同じ規律）。0 は値域外のため
            // 実害は無いが、400 の文言を「省略」と「値域外」で分けられるようにする。
            if (req.MovementThresholdRatio is not { } ratio)
            {
                return Results.BadRequest(new { error = "movementThresholdRatio（比率。0.03 ＝ ±3%）は必須です。" });
            }

            return Results.Ok(svc.UpdateMovementThreshold(ratio, MonitorSettingsEndpoints.ActorOf(http), req.Reason ?? string.Empty));
        });
}

// FR-03, FR-13, SC-01 §2, #340: 変動閾値の部分更新の要求（理由必須・FR-11）。
// 値は nullable で受け、省略を 400 に弾く（非 nullable だと省略時に既定値 0 へ暗黙束縛される）。
internal sealed record MovementThresholdUpdateRequest(decimal? MovementThresholdRatio, string? Reason);
