using MarketMonitorService.Domain;

namespace MarketMonitorService.Features.MarketMonitor.ReplaceMonitorSettings;

// FR-03, FR-11, FR-13, SC-02, #423, IADR-0164 決定3: 全置換も**部分更新と同じ規律**
// （理由必須・MonitorSettingsBounds の値域・変更履歴）を通す。導入前は理由も履歴も無く、
// 値域も「正・非負」だけだったため、**画面が弾く値を API 直叩きで保存できた**。
internal static class ReplaceMonitorSettingsEndpoint
{
    public static void MapReplaceMonitorSettings(this IEndpointRouteBuilder owner) =>
        owner.MapPut("/settings",
            (MonitorSettingsUpdateRequest req, MonitorSettingsService svc, HttpContext http) =>
            Results.Ok(svc.Replace(req.ToSettings(), MonitorSettingsEndpoints.ActorOf(http), req.Reason ?? string.Empty)));
}

// 監視設定変更の要求。MonitoredSymbols は逆直列化可能な具象 List で受ける。
// #423, IADR-0164 決定3: **値域検証はここに持たない。** 規則の単一情報源は `MonitorSettingsBounds` であり、
// `MonitorSettingsService.Replace` が通す（DTO 側に別の緩い規則を写経すると、片方だけ直したときに
// 「全置換なら通る値」が生まれる）。`Reason` は監査のため必須（空欄はサービスが 400 相当で弾く）。
internal sealed record MonitorSettingsUpdateRequest(
    decimal MovementThresholdRatio,
    TimeSpan Cooldown,
    List<MonitoredSymbol> MonitoredSymbols,
    string? Reason = null)
{
    public MarketMonitorSettings ToSettings() => new()
    {
        MovementThresholdRatio = MovementThresholdRatio,
        Cooldown = Cooldown,
        MonitoredSymbols = MonitoredSymbols ?? [],
    };
}
