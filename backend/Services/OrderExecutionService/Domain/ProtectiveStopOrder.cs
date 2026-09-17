using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Domain;

// FR-10, UC-02, #331, IADR-0210: エントリー建玉を保護する逆指値レグの記録。
// ProtectiveStopGuard の巡回対象（Active）の洗い出しと、再発注の冪等（Attempt ごとに決定的な
// StopDecisionId）の権威である。EntryDecisionId につき高々 1 行（最新試行のみを保持する）。
// ProductType / Mode / FxRateToBase は再発注・手仕舞い時に決済 Intent を再構成するために持つ
// （FxRateToBase を落とすと外貨建て決済レグが未換算で台帳へ積まれる。IADR-0107）。
//
// FR-10, ADR-0040 決定1（S1）, #820, IADR-0344 決定1: Mechanism で保護の機構を区別する（既定 S0＝ブローカー側逆指値）。
// S1（ソフトウェア逆指値）の行は StopOrderId が空（ブローカーに注文が無い）、TriggerPrice が損切りライン、
// Attempt が「送った決済の試行数」（0 始まり）、TriggeredAt / TriggeredPrice が損切りライン到達の記録（未到達は null）。
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
    DateTimeOffset UpdatedAt,
    StopLossExecutionMethod Mechanism = StopLossExecutionMethod.BrokerStopOrder,
    DateTimeOffset? TriggeredAt = null,
    decimal? TriggeredPrice = null)
{
    /// <summary>#820, IADR-0344: S1（ソフトウェア逆指値）の行か。ブローカーに注文を持たない。</summary>
    public bool IsSoftwareStop => Mechanism == StopLossExecutionMethod.SoftwareStop;

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
