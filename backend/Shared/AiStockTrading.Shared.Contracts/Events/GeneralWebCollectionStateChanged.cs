namespace AiStockTrading.Shared.Contracts.Events;

// FR-01, FR-11, #336, ADR-0020 決定4: 一般インターネット収集（最終手段）の**発動／解除**。
//
// 🔴 **発動と解除を 1 契約に置く。** 同じ「一般 Web 収集の状態が変わった」事実であり、
// 分けると受け手が 2 系統を購読し、月報の集計も 2 種の突合になる（鮮度警告で同じ判断をした・IADR-0198）。
//
// 🔴 **恒久化しない。** <see cref="ProvisionalUntil"/> は次回月報の境界であり、
// 継続が必要なら延長ではなく公式ソースへの切り替え／有料採用を ADR-0005 の判断プロセスに乗せる。
//
// Reason には発動理由（満たした条件の要旨）または解除理由を書く。裏取りの成否は Reason に含める
// （ADR-0020 決定4 は月報へ「発動理由・対象カテゴリ・期間・裏取りの成否」を記載することを求める）。
public record GeneralWebCollectionStateChanged(
    string Category,
    bool Engaged,
    string Reason,
    DateTimeOffset? ProvisionalUntil,
    DateTimeOffset OccurredAt);
