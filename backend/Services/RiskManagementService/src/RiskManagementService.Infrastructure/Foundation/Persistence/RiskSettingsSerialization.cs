using System.Text.Json;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Infrastructure.Foundation.Persistence;

// IADR-0012: RiskManagementSettings の JSON 直列化。ドメイン型の Guard は IReadOnlySet/IReadOnlyCollection を
// 用いており System.Text.Json が逆直列化時に具象化できないため、具象コレクションの永続 DTO を介して双方向変換する。
// Limits/Stage/BannedSymbol は位置/必須プロパティのレコードで、標準の直列化が往復可能。
internal static class RiskSettingsSerialization
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(RiskManagementSettings settings)
    {
        var dto = new SettingsDto(
            new GuardDto(
                [.. settings.Guard.EnabledProductTypes],
                [.. settings.Guard.EnabledMarkets],
                [.. settings.Guard.BannedSymbols],
                settings.Guard.PreventSameDayReentry,
                settings.Guard.ProhibitManipulativeOrderPatterns),
            settings.Limits,
            settings.Stage);
        return JsonSerializer.Serialize(dto, Options);
    }

    public static RiskManagementSettings Deserialize(string json)
    {
        var dto = JsonSerializer.Deserialize<SettingsDto>(json, Options)
            ?? throw new InvalidOperationException("リスク管理設定の JSON を逆直列化できませんでした。");

        var guard = new TradingGuardSettings
        {
            EnabledProductTypes = new HashSet<ProductType>(dto.Guard.EnabledProductTypes),
            EnabledMarkets = new HashSet<Market>(dto.Guard.EnabledMarkets),
            BannedSymbols = dto.Guard.BannedSymbols,
            PreventSameDayReentry = dto.Guard.PreventSameDayReentry,
            ProhibitManipulativeOrderPatterns = dto.Guard.ProhibitManipulativeOrderPatterns,
        };
        return new RiskManagementSettings(guard, dto.Limits, dto.Stage);
    }

    // 具象コレクションを持つ永続 DTO（逆直列化可能な形）。
    private sealed record SettingsDto(GuardDto Guard, RiskLimitSettings Limits, StageSettings Stage);

    private sealed record GuardDto(
        List<ProductType> EnabledProductTypes,
        List<Market> EnabledMarkets,
        List<BannedSymbol> BannedSymbols,
        bool PreventSameDayReentry,
        bool ProhibitManipulativeOrderPatterns);
}
