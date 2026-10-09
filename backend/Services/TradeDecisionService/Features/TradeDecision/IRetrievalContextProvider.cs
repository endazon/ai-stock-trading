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

    /// <summary>
    /// FR-08, FR-11, #1283: 取得し、結果に状態（成功・失敗・未構成）を添えて返す。失敗しても取得できた分（無ければ空）を返す
    /// （縮退は不変）。判断側は状態で「取得の失敗」と「参考情報が本当に無い」を区別して記録する。
    /// 既定は <see cref="GetContextAsync"/> を包んで成功とする（失敗を区別できない実装の既定）。
    /// </summary>
    async Task<RetrievalResult> GetContextWithStatusAsync(
        DecisionTrigger trigger, DailyPolicy policy, CancellationToken cancellationToken = default) =>
        new(await GetContextAsync(trigger, policy, cancellationToken).ConfigureAwait(false), RetrievalStatus.Succeeded);
}

// FR-08, FR-11, #1283: 判断文脈の取得の状態。
public enum RetrievalStatus
{
    /// <summary>取得は成功した（0 件を含む）。</summary>
    Succeeded,

    /// <summary>取得に失敗した（検索の 1 本以上が失敗・取得ポートの例外）。取得できた分だけで判断する。</summary>
    Failed,

    /// <summary>KB 検索が未構成で、取得していない。</summary>
    NotConfigured,
}

// FR-08, FR-11, #1283: 判断文脈の取得の結果と状態。
//   FailureCause   — 最初の失敗の原因の短い符号（`http-<状態コード>` / `timeout` / `exception:<型名>`）。本文・資格情報を入れない。
//   FailedSearches — 失敗した検索の本数（取得ポートの例外は 0）。
public sealed record RetrievalResult(
    IReadOnlyList<RetrievedContext> Contexts,
    RetrievalStatus Status,
    string? FailureCause = null,
    int FailedSearches = 0);

// FR-08, IADR-0072: RAG で引いた参考情報 1 件（KnowledgeHit の Application 側写像）。
//   Title       — 出典文書のタイトル。
//   Text        — 検索ヒットの本文抜粋（プロンプト側で長さを切り詰める）。
//   SourceUri   — 出典参照（あれば）。
//   Score       — 関連度スコア（並び順の参考。プロンプトには出さない）。
//   Tags        — 出所タグ（FR-04, ADR-0003, #252, IADR-0169）。**出典限定の判定に使う**。
//                 収集側は KB へ書くとき収集ソース名を載せる（KnowledgeBaseWriterSink）。空＝出所不明＝注入しない。
//   PublishedAt — 発行時刻（FR-02, FR-04, #568）。ScreeningContextAssembler が段③（古い順）の
//                 並び替え鍵として使う。KnowledgeHit と同じく取得不能なら null（最古扱いの保守側既定）。
public sealed record RetrievedContext(
    string Title,
    string Text,
    string? SourceUri = null,
    double Score = 0d,
    IReadOnlyList<string>? Tags = null,
    DateTimeOffset? PublishedAt = null)
{
    /// <summary>出所タグ（null は空として扱う。<b>空は「出所不明」であり注入しない</b>）。</summary>
    public IReadOnlyList<string> Tags { get; init; } = Tags ?? [];
}
