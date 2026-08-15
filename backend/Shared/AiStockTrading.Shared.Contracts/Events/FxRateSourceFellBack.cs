namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, FR-17, FR-09, FR-11, #381, ADR-0022 決定2, IADR-0196: 為替レートが**第一の情報源から取れず、
// フォールバック源へ切り替わった**。監査サービスが台帳へ記録し、通知サービスが Discord へ通知する。
//
// 🔴 **遷移でのみ発行する。** レート源は watchlist の銘柄ごと・巡回ごとに呼ばれるため、呼び出しごとに
// 発行すると 1 巡回で N 件出る（IADR-0096 決定4 と同型の洪水）。「使えている → 使えない」へ変わった
// 瞬間だけ発行し、続いている間は黙る。**続いていること自体は日報が期間で示す。**
//
// Quote は対象通貨（例 "USD"）。SourceName は実際に採用された源の名前（例 "fred"）。
// Rank は採用された源の優先順位（1 始まり。第一が使えていれば 1 であり、本イベントは出ない）。
public record FxRateSourceFellBack(
    string Quote,
    string SourceName,
    int Rank,
    int TotalSources,
    DateTimeOffset OccurredAt);
