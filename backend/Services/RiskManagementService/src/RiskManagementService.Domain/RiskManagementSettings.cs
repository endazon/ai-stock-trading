namespace AiStockTrading.RiskManagement.Domain;

// FR-10, FR-19, FR-20: リスク管理サービスが参照する設定の集約（設定ストア由来）
public record RiskManagementSettings(
    TradingGuardSettings Guard,
    RiskLimitSettings Limits,
    StageSettings Stage)
{
    /// <summary>
    /// FR-10, ADR-0016, #329 第 2 段階: 空売りの有効・無効と専用統制値。既定は**無効**（現物のみ）。
    /// 位置指定の引数にせず本体のプロパティに置くのは、既定が安全側（無効）に固定されており、
    /// 明示しない呼び出し（既存の設定生成・テスト）が空売りを有効化してしまわないようにするためである。
    /// </summary>
    public ShortSellSettings ShortSell { get; init; } = TradingDefaults.CreateShortSellSettings();
}
