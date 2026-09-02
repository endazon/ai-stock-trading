namespace RiskManagementService.Features.RiskManagement.GetSettingsHistory;

// FR-11: 設定変更の履歴（誰が・いつ・何を・なぜ）。
internal static class GetSettingsHistoryEndpoint
{
    public static void MapGetSettingsHistory(this IEndpointRouteBuilder owner) =>
        owner.MapGet("/settings/history", (ISettingsChangeLog changeLog) => Results.Ok(changeLog.GetHistory()));
}
