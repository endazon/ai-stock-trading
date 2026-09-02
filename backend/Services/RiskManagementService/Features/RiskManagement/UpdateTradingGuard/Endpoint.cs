using AiStockTrading.Shared.Contracts.Trading;
using RiskManagementService.Domain;

namespace RiskManagementService.Features.RiskManagement.UpdateTradingGuard;

// FR-19, #375, ADR-0021 決定4-4: 口座が対応しない商品種別（現金口座での信用買い・空売り）の有効化は
// RiskSettingsService が ArgumentException で拒否する（＝400。設定も履歴も一切変えない）。
internal static class UpdateTradingGuardEndpoint
{
    public static void MapUpdateTradingGuard(this IEndpointRouteBuilder owner) =>
        owner.MapPut("/settings/guard", (GuardUpdateRequest req, RiskSettingsService svc, HttpContext http) =>
        {
            // 口座種別の省略は「変更しない」。現行値を渡して既定値 0 への暗黙束縛を防ぐ（GuardUpdateRequest 参照）。
            var current = svc.GetCurrent().Guard.ConfiguredAccountType;
            svc.UpdateGuard(req.ToGuardSettings(current), RiskControlEndpoints.ActorOf(http), req.Reason);
            return Results.Ok(svc.GetCurrent());
        });
}

// ガード変更の要求。TradingGuardSettings は IReadOnlySet 等を用いるため、逆直列化可能な具象コレクションで受ける。
internal sealed record GuardUpdateRequest(
    List<ProductType> EnabledProductTypes,
    List<Market> EnabledMarkets,
    List<BannedSymbol> BannedSymbols,
    bool PreventSameDayReentry,
    bool ProhibitManipulativeOrderPatterns,
    string Reason,
    // FR-19, #375, ADR-0021 決定3: 利用者が設定した口座種別。
    // **省略（null）は「変更しない」**であり、既定値 0（＝信用口座）へ暗黙束縛しない。本エンドポイントは
    // 全置換 PUT であり、非 nullable enum で受けると**送り漏らした瞬間に口座種別が信用口座へ戻る**
    // （禁止銘柄を 1 件足しただけで口座種別の設定が消える）。BrokerProviderUpdateRequest.Provider と同じ規律である。
    AccountType? ConfiguredAccountType = null)
{
    public TradingGuardSettings ToGuardSettings(AccountType currentAccountType) => new()
    {
        EnabledProductTypes = new HashSet<ProductType>(EnabledProductTypes),
        EnabledMarkets = new HashSet<Market>(EnabledMarkets),
        BannedSymbols = BannedSymbols,
        PreventSameDayReentry = PreventSameDayReentry,
        ProhibitManipulativeOrderPatterns = ProhibitManipulativeOrderPatterns,
        ConfiguredAccountType = ConfiguredAccountType ?? currentAccountType,
    };
}
