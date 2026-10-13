using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution.QueryInFlightCloses;

// 🔴 FR-04, FR-10, #1338, IADR-0535 決定2: 処理中の決済の照会結果（`GET /order-execution/in-flight-closes` の応答）。
//
// 🔴 **「分からない」（Unknown）と「覆わない」（NotCovered）を分ける**（原則 A）。受け手（取引判断）は Covered のときだけ、
// 決済の結論が発注にならないと読む（ほかの結論も発注にならないときだけ LLM を省く）。Unknown・NotCovered は LLM を呼ぶ。
//
// Symbol・Market・CloseSide は要求の写し（受け手が「別の銘柄・方向の答えを読んでいない」ことを確かめるため）。
// ClosableQuantity はブローカーの決済方向の建玉（照会したときだけ。照会していなければ null）、InFlightQuantity は処理中の決済の残り株数。
// OrderIds・DecisionIds は数えた処理中の決済（同じ並び）。
public sealed record InFlightClosesView(
    string Symbol,
    Market Market,
    TradeSide CloseSide,
    InFlightClosesStatus Status,
    string? UnknownReason,
    int? ClosableQuantity,
    int InFlightQuantity,
    IReadOnlyList<string> OrderIds,
    IReadOnlyList<Guid> DecisionIds);

// 🔴 **既定値（0）は Unknown である。** 項目の欠落・読み違いが「覆う」（＝LLM を省く）へ倒れないようにする。
public enum InFlightClosesStatus
{
    Unknown = 0,
    Covered = 1,
    NotCovered = 2,
}

// Unknown の理由（ログで「なぜ分からないか」を読むための語彙。受け手は判定に使わない）。
public static class InFlightClosesUnknownReasons
{
    /// <summary>発注先が建玉照会を持たない（内蔵 paper）か、保護記録ストアが無い。決済のゲートもこの構成では見送らない。</summary>
    public const string BrokerNotSupported = "broker-not-supported";

    /// <summary>処理中の決済の記録・保護記録を読めなかった。</summary>
    public const string RecordsUnavailable = "records-unavailable";

    /// <summary>ブローカーの建玉を照会できなかった（null・例外）。</summary>
    public const string PositionsIndeterminate = "positions-indeterminate";
}
