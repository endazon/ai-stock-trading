namespace AiStockTrading.RiskManagement.Domain;

// FR-10, FR-19, FR-20: リスク管理サービスが参照する設定の集約（設定ストア由来）
public record RiskManagementSettings(
    TradingGuardSettings Guard,
    RiskLimitSettings Limits,
    StageSettings Stage);
