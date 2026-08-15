namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, FR-17, FR-09, FR-11, #381, ADR-0022 決定2, IADR-0196: 為替レートの**第一の情報源が回復した**。
//
// 🔴 **本イベントが「切り替わっていた期間」を運ぶ。** ADR-0022 決定2 は事実だけでなく
// **「切り替わっていた期間」**の記録を求めている。<see cref="FellBackAt"/> を持たせることで、
// **受け手が別のイベントを探して引き算する必要が無い** —— 期間は発行側が事実として渡す。
//
// 受け手が引き算する形にすると、片方のイベントを取りこぼしたときに**期間が黙って間違う**。
// 「回復した」という 1 件を見れば期間が分かる形にしておく。
public record FxRateSourcePrimaryRestored(
    string Quote,
    string SourceName,
    DateTimeOffset FellBackAt,
    DateTimeOffset OccurredAt)
{
    /// <summary>フォールバックしていた期間。<b>負にはならない</b>（発行側が遷移で作るため）。</summary>
    public TimeSpan FallbackDuration => OccurredAt - FellBackAt;
}
