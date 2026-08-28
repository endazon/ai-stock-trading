namespace AiStockTrading.Shared.Contracts.Events;

// FR-06, FR-10, FR-11, FR-17, #513, ADR-0022 決定1, IADR-0225: 為替レートを**どの情報源から取ったか**の記録。
// **暦日ごと・通貨ごと・源ごとに 1 件**だけ発行する（当日の初回使用）。
//
// 🔴 **遷移の記録（FxRateSourceFellBack / FxRateSourcePrimaryRestored）では平常時が空白になる。**
// 遷移でしか発行しないため（IADR-0196 決定1）、切替も復帰も起きない「静かな期間」は台帳に何も残らず、
// **「静かに第一の源を使った」と「為替を一度も使わなかった」の区別が付かない**——
// その結果、日報の出典が平常時こそ「記録からは特定できません」になっていた（IADR-0199 決定5）。
//
// 🔴 **それでも呼び出しごとには発行しない。** レート源は watchlist の銘柄ごと・巡回ごとに呼ばれる。
// 抑止の鍵は **(通貨, 源, 暦日)** であり、**同じ日に同じ源を何度使っても 1 件**である
// （鮮度警告の暦日抑止〔IADR-0198 決定2〕と同じ形。暦日は UTC）。
//
// Quote は対象通貨（例 "USD"）。SourceName は実際に採用された源の名前（例 "boj"）。
// Rank は採用された源の優先順位（1 始まり。**1 なら第一の情報源を使った証拠になる**）。
public record FxRateSourceUsed(
    string Quote,
    string SourceName,
    int Rank,
    int TotalSources,
    DateTimeOffset OccurredAt)
{
    /// <summary>第一の情報源から取得したか（ADR-0022 決定1 の「第一＝日銀」を使ったことの証拠）。</summary>
    public bool IsPrimary => Rank <= 1;
}
