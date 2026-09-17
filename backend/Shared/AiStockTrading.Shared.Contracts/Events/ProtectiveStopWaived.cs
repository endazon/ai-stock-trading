using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, FR-12, FR-11, ADR-0040 決定1, #819, IADR-0342 決定6: moomoo SIMULATE で損切りの実行機構 S2
// （逆指値なしの建玉を許容）が選ばれていたため、新規建てに**保護逆指値を発注せず建玉を保持した**（ペーパーで免除）。
//
// 🔴 **`ProtectiveStopCoverageLost` とは別の事実である。** あちらは「逆指値が成立しなかったので建玉を解消した
// （または解消に失敗した）」という**統制が働いた／破れた**記録であり、こちらは「利用者の選択により
// 逆指値を最初から置かなかった」という**免除の記録**である。同じイベントの値で表すと、
// 監査台帳の「建玉あり ⇒ 有効な逆指値あり（または解消済み）」の読みが崩れる。
//
// - エントリーが生きている（Accepted / PartiallyFilled / Filled）ときだけ発行する（建玉が生じない注文に免除は無い）。
// - Method は承認が運んだ手法（現状は常に S2）、Provider は実際に発注したアダプタの発注先（現状は常に moomoo SIMULATE）。
// - StopLossPrice は判断が付けた損切りライン（ブローカーへは置いていない）。
public record ProtectiveStopWaived(
    Guid EntryDecisionId,
    string Symbol,
    Market Market,
    TradeSide Side,
    ProductType ProductType,
    int Quantity,
    decimal? StopLossPrice,
    StopLossExecutionMethod Method,
    BrokerProvider Provider,
    DateTimeOffset OccurredAt);
