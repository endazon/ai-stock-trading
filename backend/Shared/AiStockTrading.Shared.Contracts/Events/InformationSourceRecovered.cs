namespace AiStockTrading.Shared.Contracts.Events;

// FR-01, FR-09, FR-11, #336, ADR-0020 決定2-3: 欠測していた情報源のまとまりが**回復した**。
//
// 🔴 **本イベントが「欠測していた期間」と「該当サイクル数」を運ぶ。** ADR-0020 決定2-3 は
// 「欠測の発生時刻・継続時間・該当サイクル数を日報に、月次合計を月報に記録する」ことを求めている。
// **受け手が別のイベントを探して引き算する形にしない** —— 片方を取りこぼすと期間が黙って間違う
// （為替の復帰イベントと同じ規律・IADR-0196）。
public record InformationSourceRecovered(
    string Category,
    DateTimeOffset DegradedAt,
    int AffectedCycles,
    DateTimeOffset OccurredAt)
{
    /// <summary>欠測していた期間。<b>負にはならない</b>（発行側が遷移で作るため）。</summary>
    public TimeSpan OutageDuration => OccurredAt - DegradedAt;
}
