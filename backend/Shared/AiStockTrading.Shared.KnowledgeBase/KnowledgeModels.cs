namespace AiStockTrading.Shared.KnowledgeBase;

// FR-08, IADR-0069: KB 保存・RAG 取得の当リポ側 DTO（platform の Knowledge.Contracts へ直接依存しない疎な境界）。
// platform 契約への写像は HTTP アダプタの内側にのみ閉じる。

// FR-08: 機密区分（microservices-platform IADR-0047 の正準値。本リポの IADR-0047 とは別採番）。保存時に必須のため、未指定は既定 Internal を補完する。
public static class KnowledgeConfidentiality
{
    public const string Public = "public";
    public const string Internal = "internal";
    public const string Confidential = "confidential";
    public const string Restricted = "restricted";

    // 呼び出し側未指定・空のときの安全既定。取引の収集情報・判断根拠は社外秘扱いが妥当なため internal。
    public const string Default = Internal;
}

// FR-08: 必須属性 owner / department の予約値（planning#344 確定・
// project-planning/projects/microservices-platform/10_feedback/20260815_ingestion-owner-department-resolution.md）。
// 「解決できなかった」ことの記録であり既定値ではない（platform 側 measure-abac-combinations.js が
// 環流債務として件数を観測する）。#520 での判断根拠は作業仕様書 20260828_520 を参照。
public static class KnowledgeAttributeDefaults
{
    // owner: AST は無人のバッチ実行であり、解決できる利用者主体が存在しない（更新者を運ぶ器が無い）。
    public const string ReservedOwner = "system";

    // department: AST を表す固有の部門コードは計画側に存在しない。部門コードの値域自体が
    // 組織側の取り決めとして未確定（project-planning/projects/microservices-platform/06_technical/
    // 09_datasource-connectors.md §未確定事項「値域が定まるまで department の写像は行わない」）。
    // 推測で値を決めず、予約値へ倒す。
    public const string UnassignedDepartment = "unassigned";
}

// FR-08: KB へ保存する 1 文書。Title/属性/タグはカタログ登録に用いる。
//   Content     — 正規化 Markdown 本文（将来のオブジェクトストレージ取り込み用。現行の POST /documents は本文を受けない）。
//   SourceUri   — 元情報への参照（platform 側 OriginalUri に写像）。
//   Confidentiality — 機密区分（未指定は既定 internal）。
//   Attributes  — 追加の ABAC 属性（confidentiality は Confidentiality から補完し上書きしない）。
public sealed record KnowledgeDocument(
    string Title,
    string? Content = null,
    string Confidentiality = KnowledgeConfidentiality.Default,
    IReadOnlyList<string>? Tags = null,
    string? SourceUri = null,
    string? ContentType = null,
    IReadOnlyDictionary<string, string>? Attributes = null);

// FR-08: 保存結果。Saved=false は fail-safe 縮退（未保存）を表し、例外は投げない。
public sealed record KnowledgeWriteResult(bool Saved, Guid? DocumentId)
{
    public static readonly KnowledgeWriteResult NotSaved = new(false, null);

    public static KnowledgeWriteResult Ok(Guid documentId) => new(true, documentId);
}

// FR-08: RAG 検索クエリ。AttributeFilters は単値完全一致（platform SearchRequest.AttributeFilters に写像）。
public sealed record KnowledgeQuery(
    string Query,
    int TopK = 8,
    IReadOnlyDictionary<string, string>? AttributeFilters = null);

// FR-08: RAG 検索ヒット 1 件（チャンク単位。platform SearchResultDto に対応）。
public sealed record KnowledgeHit(
    Guid DocumentId,
    string DocumentTitle,
    string Text,
    double Score,
    string? SourceUri,
    IReadOnlyList<string> Tags);
