using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-05, FR-10, #292, IADR-0118: 発注執行サービスがブローカへ照会して観測した建玉一覧。
//
// 「照会できなかった（不明）」は本イベントを**発行しない**ことで表す。空の Positions は「ブローカに建玉が無い」
// という観測事実であり、不明とは意味が異なる（取り違えると台帳の全建玉が乖離として報告される）。
public record BrokerPositionsObserved(
    IReadOnlyList<BrokerPositionSnapshot> Positions,
    DateTimeOffset ObservedAt);
