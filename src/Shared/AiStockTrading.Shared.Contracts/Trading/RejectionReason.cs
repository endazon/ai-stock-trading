namespace AiStockTrading.Shared.Contracts.Trading;

// FR-10, FR-11, FR-19, FR-20: 発注拒否の理由コード。監査ログ・Discord 通知で利用する
public enum RejectionReason
{
    KillSwitchActive,
    StageProhibitsLiveTrading,
    StageCapitalCapExceeded,
    ProductTypeDisabled,
    MarketDisabled,
    BannedSymbol,
    SameDayReentry,
    PerOrderAmountExceeded,
    DailyOrderAmountExceeded,
    MaxPositionsExceeded,
    DailyLossLimitReached,
    MaxDrawdownReached,
}
