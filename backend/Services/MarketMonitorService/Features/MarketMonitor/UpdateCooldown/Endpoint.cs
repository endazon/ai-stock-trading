namespace MarketMonitorService.Features.MarketMonitor.UpdateCooldown;

// ---- 収集パラメータの部分更新（FR-03/FR-11/FR-13, UC-06, SC-01 §2, #340, IADR-0155）----
internal static class UpdateCooldownEndpoint
{
    public static void MapUpdateCooldown(this IEndpointRouteBuilder owner) =>
        owner.MapPut("/settings/cooldown",
            (CooldownUpdateRequest req, MonitorSettingsService svc, HttpContext http) =>
        {
            if (req.Cooldown is not { } cooldown)
            {
                return Results.BadRequest(new { error = "cooldown（HH:mm:ss）は必須です。" });
            }

            return Results.Ok(svc.UpdateCooldown(cooldown, MonitorSettingsEndpoints.ActorOf(http), req.Reason ?? string.Empty));
        });
}

// FR-03, FR-13, SC-01 §2, #340: クールダウンの部分更新の要求（理由必須・FR-11）。
internal sealed record CooldownUpdateRequest(TimeSpan? Cooldown, string? Reason);
