using System.Text.Json;
using AiStockTrading.MarketMonitor.Domain;

namespace AiStockTrading.MarketMonitor.Worker.Foundation.Persistence;

// IADR-0012 踏襲: MarketMonitorSettings の JSON 直列化。MonitoredSymbols は IReadOnlyCollection のため
// System.Text.Json が逆直列化時に具象化できない。具象 List の永続 DTO を介して双方向変換する。
internal static class MonitorSettingsSerialization
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(MarketMonitorSettings settings)
    {
        var dto = new SettingsDto(
            settings.MovementThresholdRatio,
            settings.Cooldown,
            [.. settings.MonitoredSymbols]);
        return JsonSerializer.Serialize(dto, Options);
    }

    public static MarketMonitorSettings Deserialize(string json)
    {
        var dto = JsonSerializer.Deserialize<SettingsDto>(json, Options)
            ?? throw new InvalidOperationException("市場監視設定の JSON を逆直列化できませんでした。");

        return new MarketMonitorSettings
        {
            MovementThresholdRatio = dto.MovementThresholdRatio,
            Cooldown = dto.Cooldown,
            MonitoredSymbols = dto.MonitoredSymbols,
        };
    }

    private sealed record SettingsDto(
        decimal MovementThresholdRatio,
        TimeSpan Cooldown,
        List<MonitoredSymbol> MonitoredSymbols);
}
