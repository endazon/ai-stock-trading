using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-20, ADR-0008: 運用段階（段階ゲート）。遷移は合格・撤退基準に基づき利用者の承認で行う
public enum TradingStage
{
    Stage0Verification,
    Stage1Paper,
    Stage2MinimalLive,
    Stage3ScaledLive,
}

// FR-20: 段階ごとの動作モード（ペーパー/実弾）と資金上限を強制する
public record StageSettings(
    TradingStage Stage,
    TradeMode Mode,
    decimal CapitalCap);
