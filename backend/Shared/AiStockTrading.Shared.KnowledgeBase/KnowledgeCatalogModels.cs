namespace AiStockTrading.Shared.KnowledgeBase;

// FR-08, #1028, IADR-0436 決定 2: 基盤の文書台帳（DocumentService）を**保守の操作**から読む・書くための当リポ側 DTO。
//
// 🔴 **保存ポート（IKnowledgeBaseWriter）の Saved=false とは違い、4 つの結果を分ける（原則 A）。**
// 業務経路の保存は best-effort で「保存されなかった」の 1 値で足りるが、入れ直しの操作は
// 「失敗した（届いたが拒否された・届かなかった）」と「結果が分からない（送った後に応答が来なかった）」を
// 取り違えると、再実行で重複を作るか、直ったものを直っていないと報告する。
public enum KnowledgeCatalogOutcome
{
    /// <summary>成功（応答で確かめた）。</summary>
    Succeeded,

    /// <summary>KB が構成されていない（KnowledgeBase:Documents:BaseUrl 未設定）。何も送っていない。</summary>
    NotConfigured,

    /// <summary>失敗（拒否された・届いていない）。書き込みは起きていない。</summary>
    Failed,

    /// <summary>結果が分からない（送った後のタイムアウト・切断・5xx）。書き込みが起きたかもしれない。</summary>
    Unknown,
}

// FR-08, #1028: 台帳の 1 文書（一覧の行）。HasStoredBody は基盤が本文の実体を持つか
// （`MarkdownUri` の有無。基盤の `HasBody` は本文なしで作った文書でも既定 true のため使わない）。
public sealed record KnowledgeCatalogEntry(
    Guid DocumentId,
    string Title,
    IReadOnlyDictionary<string, string> Attributes,
    bool HasStoredBody,
    DateTimeOffset UpdatedAt);

// FR-08, #1028: 一覧の結果。Entries は Succeeded のときだけ意味を持つ（それ以外は空）。
public sealed record KnowledgeCatalogListResult(
    KnowledgeCatalogOutcome Outcome,
    IReadOnlyList<KnowledgeCatalogEntry> Entries,
    string? Reason)
{
    public static KnowledgeCatalogListResult Ok(IReadOnlyList<KnowledgeCatalogEntry> entries) =>
        new(KnowledgeCatalogOutcome.Succeeded, entries, null);

    public static readonly KnowledgeCatalogListResult NotConfigured =
        new(KnowledgeCatalogOutcome.NotConfigured, [], "KB（KnowledgeBase:Documents:BaseUrl）が構成されていません。");

    public static KnowledgeCatalogListResult Failed(string reason) => new(KnowledgeCatalogOutcome.Failed, [], reason);

    public static KnowledgeCatalogListResult Unknown(string reason) => new(KnowledgeCatalogOutcome.Unknown, [], reason);
}

// FR-08, #1028: 書き込み（作成・本文の投入）の結果。DocumentId は Succeeded のときの対象文書。
public sealed record KnowledgeCatalogWriteResult(
    KnowledgeCatalogOutcome Outcome,
    Guid? DocumentId,
    string? Reason,
    int? StatusCode = null)
{
    public static KnowledgeCatalogWriteResult Ok(Guid documentId) => new(KnowledgeCatalogOutcome.Succeeded, documentId, null);

    public static readonly KnowledgeCatalogWriteResult NotConfigured =
        new(KnowledgeCatalogOutcome.NotConfigured, null, "KB（KnowledgeBase:Documents:BaseUrl）が構成されていません。");

    public static KnowledgeCatalogWriteResult Failed(string reason, int? statusCode = null) =>
        new(KnowledgeCatalogOutcome.Failed, null, reason, statusCode);

    // 基盤が 404 で拒否した（文書が無いか、呼び出し元が所有者ではない。基盤は両者を区別しない）。
    public bool IsNotFoundOrNotOwner => Outcome == KnowledgeCatalogOutcome.Failed && StatusCode == 404;

    public static KnowledgeCatalogWriteResult Unknown(string reason) => new(KnowledgeCatalogOutcome.Unknown, null, reason);
}
