using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.TradeDecision.Application.Ports;

// FR-04, FR-10, IADR-0003/0017: サイジングに必要な文脈（資金・リスク設定・段階/日次残枠・連敗/DD・動作モード）。
// 実データは設定（#12）・保有/約定（#12/#13）連携で供給する。
public interface ISizingContextProvider
{
    SizingContext GetContext();
}

// サイジング文脈。availableCapital は段階資金残枠と日次発注残枠の小さい方を用いる（IADR-0017）。
public record SizingContext(
    decimal Capital,
    decimal StageCapitalRemaining,   // CapitalCap − InvestedCapital（段階資金の残枠）
    decimal DailyOrderRemaining,     // MaxDailyOrderAmount − DailyOrderedAmount（当日発注の残枠）
    int ConsecutiveLosses,
    decimal DrawdownRatio,
    TradeMode Mode,
    RiskLimitSettings Limits);
