using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, FR-06, FR-11, ADR-0040 決定1, #1002, IADR-0429 決定1・決定2: 発注執行が新規建ての承認について
// **損切りの実行機構を解決した結果**（承認が運んだ手法 → 実際に適用した手法）。
//
// 日報の「実際に適用された手法（発注執行の解決結果）」と月報 §6 の日数ベースの内訳の一次記録である
// （計画 04_report-templates・planning#644 の裁定 1・2）。報告書は監査台帳からこれを引き、承認と DecisionId で突き合わせる。
//
// - 発行するのは **Open（新規建て）の承認を解決した回だけ**である。Close・完了済みの承認の再配送・見送り済みの承認の再配送は
//   解決しないので出ない。解決の後に見送った回（逆指値価格なし等）でも出る——解決結果そのものは事実である。
// - **発注の成否ではない。** 解決の後の見送り・S3 の代替注文の拒否・約定の有無は本イベントに現れない。
// - `SelectedMethod` は承認が運んだ手法（未知の値もそのまま運ぶ）。`AppliedMethod` は実際に適用した手法で、
//   **発注しない（拒否）なら null**。`Reason` は両者が違う理由（一致なら AsSelected）。
// - `Provider` は**実際に発注するアダプタ**の発注先（設定上の発注先ではない。IADR-0413 決定1）。
public record StopLossMethodResolved(
    Guid DecisionId,
    string Symbol,
    Market Market,
    ProductType ProductType,
    StopLossExecutionMethod SelectedMethod,
    StopLossExecutionMethod? AppliedMethod,
    StopLossMethodResolutionReason Reason,
    BrokerProvider Provider,
    DateTimeOffset OccurredAt);
