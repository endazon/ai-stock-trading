using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement.GetInFlightOrders;

// NFR, #1502, IADR-0600: `GET /risk-controls/in-flight-orders` の 1 行。門の判定に要る最小の欄だけを持つ
// （銘柄・種別・状態・承認時刻）。🔴 口座の識別子・数量・価格・金額・判断 ID は返さない。
public sealed record InFlightOrderView(
    string Symbol,
    Market Market,
    InFlightOrderKind Kind,
    InFlightOrderState State,
    DateTimeOffset ApprovedAt);

/// <summary>未終端の注文の種別。</summary>
public enum InFlightOrderKind
{
    /// <summary>新規建て（約定待ち）。</summary>
    Entry = 1,

    /// <summary>決済（手仕舞い中）。</summary>
    Close = 2,
}
