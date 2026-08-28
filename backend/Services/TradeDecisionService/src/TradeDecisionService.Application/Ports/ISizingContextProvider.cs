using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Application.Ports;

// FR-04, FR-10, IADR-0003/0017/0029: サイジングに必要な文脈（資金・リスク設定・段階/日次残枠・連敗/DD・動作モード）。
// 実データはリスク管理（#12）の GET /risk-controls/sizing-context を同期照会して供給する（HttpSizingContextProvider）。
// 同期 HTTP を sync-over-async にしないため非同期とする。依存先障害時は残枠 0 の安全既定（＝取引しない）に倒す。
public interface ISizingContextProvider
{
    Task<SizingContext> GetContextAsync(CancellationToken cancellationToken = default);
}

// サイジング文脈。availableCapital は段階資金残枠と日次発注残枠の小さい方を用いる（IADR-0017）。
public record SizingContext(
    decimal Capital,
    decimal StageCapitalRemaining,   // CapitalCap − InvestedCapital（段階資金の残枠）
    decimal DailyOrderRemaining,     // MaxDailyOrderAmount − DailyOrderedAmount（当日発注の残枠）
    int ConsecutiveLosses,
    decimal DrawdownRatio,
    BrokerProvider Mode,
    RiskLimitSettings Limits);
