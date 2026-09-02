using System.Text;

namespace AiStockTrading.Shared.KnowledgeBase;

// FR-08, IADR-0069: KB 保存・RAG 取得の当リポ側 DTO（platform の Knowledge.Contracts へ直接依存しない疎な境界）。
// platform 契約への写像は HTTP アダプタの内側にのみ閉じる。

// FR-08, #565: 本文（Markdown）を POST /documents へ渡すときの上限判定。
// **上限値は platform DocumentService.Domain.DocumentBodyIntake.MaxBytes（1 MB）と同値**——
// 送信側で緩く・受信側で厳しいと、いつも 413 を引いて未保存に倒れる（無駄な往復）。
// 判定は **UTF-8 のバイト数**で行う（文字数で測ると日本語本文が実サイズの 3 分の 1 で通り、上限が事実上 3 MB へ化ける。
// platform 側と同じ理由）。純関数として切り出し、境界値をテストで固定する。
public static class KnowledgeBodyLimits
{
    public const int MaxBytes = 1024 * 1024;

    public static bool Exceeds(string? content) =>
        !string.IsNullOrEmpty(content) && Encoding.UTF8.GetByteCount(content) > MaxBytes;
}

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
//   Content     — 正規化 Markdown 本文。#565, IADR-0272: POST /documents の Body として送る
//                 （platform 側がオブジェクトストレージへ格納し Ingestion が索引する。IADR-0069 のスコープ境界は解消済み）。
//                 1 MB（UTF-8 バイト数。KnowledgeBodyLimits.Exceeds）超は送らず、メタデータのみで登録する。
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

// FR-08, FR-02, FR-04, #568: RAG 検索ヒット 1 件（チャンク単位。platform SearchResultDto に対応）。
//   PublishedAt — 元記事・開示の発行時刻（ScreeningContextPlanner 段③「古い順」の並び替え鍵。
//   IADR-0247 残余リスクの解消・IADR-0270）。platform 契約の `SearchResultDto.UpdatedAt`（索引の
//   更新時刻）とは意味が異なるため流用しない——本項目は AST 書き込み側（KnowledgeBaseWriterSink）が
//   ABAC 属性 `publishedAt` として書いた値を検索応答の Attributes から復元したものである
//   （供給できない・解釈できない場合は null＝最古扱いの保守側既定。捏造しない）。
public sealed record KnowledgeHit(
    Guid DocumentId,
    string DocumentTitle,
    string Text,
    double Score,
    string? SourceUri,
    IReadOnlyList<string> Tags,
    DateTimeOffset? PublishedAt = null);
