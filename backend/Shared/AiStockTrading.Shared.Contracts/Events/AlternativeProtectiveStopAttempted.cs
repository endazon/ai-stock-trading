using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, FR-11, FR-12, ADR-0040 決定1（S3）, #821, IADR-0347: 損切りの実行機構 S3（他のブローカー側注文種別）で
// 保護レグを**試した**という事実。**受理・拒否のどちらでも 1 件出す。**
//
// 🔴 **本イベントの存在理由は「何の注文種別で試し、どう断られたか」を監査台帳へ残すことである**（#821 の目的そのもの）。
// 公式は模擬取引を「指値・成行のみ」としており、S3 は拒否される見込みが高い。拒否理由をアダプタのログだけに残すと、
// 7 年保持される監査台帳（NFR-10）から「なぜ S3 が使えないのか」を後から読めない。
//
// - `OrderType` は実際に送った種別（StopLimit / TrailingStop。構成で決まる）。
// - `Status` はブローカー注文の状態（拒否は Rejected）。`BrokerOrderId` は発注できたときのブローカー注文 ID。
//   #842, IADR-0405: **送信しなかった・ブローカーが受理しなかった場合は null**（アダプタが合成した ID を載せない）。
// - #842, IADR-0405: `RejectReasonMessage` は監査台帳へ入る前に上限（500 文字）と伏せ字（接続先）で整えられる
//   （AuditService の AuditFreeText）。本イベント自体は整える前の文面を運ぶ（発行側のログは従来どおり）。
// - `RejectReasonCode` は moomoo の retType、`RejectReasonMessage` は retMsg（送信前棄却・送信後例外はその理由）。
//   **受理された場合は両方 null** である。
//
// 🔴 **`ProtectiveStopPlaced` / `ProtectiveStopCoverageLost` を置き換えない。** S3 の結果の扱いは S0 と同一であり
// （受理＝保護レグとして記録／拒否＝建玉を持たない）、本イベントはその**手前の試行の記録**として重ねて出る。
public record AlternativeProtectiveStopAttempted(
    Guid EntryDecisionId,
    Guid StopDecisionId,
    string Symbol,
    Market Market,
    AlternativeProtectiveOrderType OrderType,
    OrderStatus Status,
    string? BrokerOrderId,
    int? RejectReasonCode,
    string? RejectReasonMessage,
    StopLossExecutionMethod Method,
    BrokerProvider Provider,
    DateTimeOffset OccurredAt);
