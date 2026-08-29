using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Features.TradeDecision;

// FR-08, FR-04, ADR-0003, IADR-0072: 判断文脈への RAG 取得ポート（Application 抽象）。
// 実体は #18（IADR-0069）の RAG 取得ポート IKnowledgeBaseSearch を包む Worker アダプタ（KnowledgeBaseRetrievalContextProvider）。
// Application/Domain を Shared.KnowledgeBase の DTO へ直結させないため、IDailyPolicyProvider 等と同じ「ポート／アダプタ」分離を採る。
// 安全既定: 実接続は KnowledgeBase:Search:BaseUrl で opt-in。既定は NoOpRetrievalContextProvider（常に空＝文脈なし＝現行動作）。
// fail-safe: 取得失敗・空は空リストに縮退し、判断側は「文脈なし」で継続する（判断を止めない）。
public interface IRetrievalContextProvider
{
    Task<IReadOnlyList<RetrievedContext>> GetContextAsync(
        DecisionTrigger trigger, DailyPolicy policy, CancellationToken cancellationToken = default);
}

// FR-08, IADR-0072: RAG で引いた参考情報 1 件（KnowledgeHit の Application 側写像）。
//   Title     — 出典文書のタイトル。
//   Text      — 検索ヒットの本文抜粋（プロンプト側で長さを切り詰める）。
//   SourceUri — 出典参照（あれば）。
//   Score     — 関連度スコア（並び順の参考。プロンプトには出さない）。
//   Tags      — 出所タグ（FR-04, ADR-0003, #252, IADR-0169）。**出典限定の判定に使う**。
//               収集側は KB へ書くとき収集ソース名を載せる（KnowledgeBaseWriterSink）。空＝出所不明＝注入しない。
public sealed record RetrievedContext(
    string Title,
    string Text,
    string? SourceUri = null,
    double Score = 0d,
    IReadOnlyList<string>? Tags = null)
{
    /// <summary>出所タグ（null は空として扱う。<b>空は「出所不明」であり注入しない</b>）。</summary>
    public IReadOnlyList<string> Tags { get; init; } = Tags ?? [];
}
