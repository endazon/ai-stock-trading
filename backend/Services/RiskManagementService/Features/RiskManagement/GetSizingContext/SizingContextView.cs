using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement.GetSizingContext;

// FR-04, FR-10, IADR-0029: 取引判断のサイジングに供給する文脈（設定＋ポートフォリオ状態から導出）。
// 取引判断（#11）の SizingContext と同形。段階/日次残枠は負値を 0 にクランプ済み。
public sealed record SizingContextView(
    decimal Capital,
    decimal StageCapitalRemaining,
    decimal DailyOrderRemaining,
    int ConsecutiveLosses,
    decimal DrawdownRatio,
    BrokerProvider Mode,
    RiskLimitSettings Limits);
