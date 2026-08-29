using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Domain;

// FR-10, UC-02, #331, IADR-0210: エントリー建玉を保護する逆指値レグの記録。
// ProtectiveStopGuard の巡回対象（Active）の洗い出しと、再発注の冪等（Attempt ごとに決定的な
// StopDecisionId）の権威である。EntryDecisionId につき高々 1 行（最新試行のみを保持する）。
// ProductType / Mode / FxRateToBase は再発注・手仕舞い時に決済 Intent を再構成するために持つ
// （FxRateToBase を落とすと外貨建て決済レグが未換算で台帳へ積まれる。IADR-0107）。
public record ProtectiveStopOrder(
    Guid EntryDecisionId,
    Guid StopDecisionId,
    string StopOrderId,
    string Symbol,
    Market Market,
    TradeSide EntrySide,
    ProductType ProductType,
    BrokerProvider Mode,
    int Quantity,
    decimal TriggerPrice,
    decimal FxRateToBase,
    int Attempt,
    ProtectiveStopState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>決済方向（エントリーの反対売買）。ロング（Buy 建て）は Sell、ショート（Sell 建て）は Buy。</summary>
    public TradeSide CloseSide => EntrySide == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;
}

// #331, IADR-0210: 保護逆指値の状態。Active のみが巡回対象。
public enum ProtectiveStopState
{
    /// <summary>ブローカーに滞留中（建玉を保護している）。</summary>
    Active = 0,

    /// <summary>保護の役目を終えた（逆指値約定・建玉消滅・手仕舞い済み等）。理由は監査イベント側に残る。</summary>
    Completed = 1,
}
