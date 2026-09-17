using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, FR-12, FR-11, ADR-0040 決定1（S1）, #820, IADR-0344 決定3・決定8: moomoo SIMULATE で損切りの実行機構 S1
// （ソフトウェア逆指値）が選ばれていたため、新規建てに**ブローカーの保護逆指値を発注せず**、発注執行がソフトウェア逆指値を
// 永続化した。損切りライン到達（StopLossTriggered）で発注執行が成行決済する（結果は SoftwareStopExecuted）。
//
// 🔴 **ブローカー側に保護は無い。** 発注執行・市場監視・メッセージ基盤のいずれかが止まっている間は決済されない。
// ProtectiveStopPlaced（ブローカーに逆指値が滞留）・ProtectiveStopWaived（S2・誰も決済しない）とは別の事実である。
//
// - エントリーが生きている（Accepted / PartiallyFilled / Filled）ときだけ発行する。
// - Quantity は承認数量（決済は約定数量と建玉残の小さい方）。Provider は実際に発注したアダプタの発注先（現状は常に moomoo SIMULATE）。
public record SoftwareStopArmed(
    Guid EntryDecisionId,
    string Symbol,
    Market Market,
    TradeSide Side,
    ProductType ProductType,
    int Quantity,
    decimal StopLossPrice,
    BrokerProvider Provider,
    DateTimeOffset OccurredAt);
