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

    // FR-08, ADR-0032 決定2(1), IADR-0293: 本ユニットが基盤へ保存する文書は project 属性に
    // この値を必須で持つ（platform 07_abac-attribute-model.md の project＝プロジェクトコード。単値）。
    // owner/department と異なり許容値は単一のため、異なる明示指定は補完せず拒否する
    // （HttpKnowledgeBaseWriter.BuildAttributes 参照）。
    public const string ProjectKey = "project";
    public const string RequiredProject = "ai-stock-trading";
}

// FR-08: KB へ保存する 1 文書。Title/属性/タグはカタログ登録に用いる。
//   Content     — 正規化 Markdown 本文。#565, IADR-0274: POST /documents の Body として送る
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
//   SortBy — 並び順（platform SearchRequest.SortBy に写像。値は KnowledgeSearchSorts）。null は基盤の既定（関連度順）。
//   #1083, IADR-0454 決定4: 基盤の `updated` は**取得後の並べ替え**（関連度の候補 4×TopK の中で索引の更新日時の降順）で、
//   関連度は候補の門番として残る。
public sealed record KnowledgeQuery(
    string Query,
    int TopK = 8,
    IReadOnlyDictionary<string, string>? AttributeFilters = null,
    string? SortBy = null);

// FR-08, #1083, IADR-0454 決定4: 並び順の値（platform Knowledge.Contracts.Dtos.SearchSorts と同じ文字列。
// 基盤は未知の値を関連度順へ縮退させるため、ここで値を増やしても壊れはしないが効きもしない）。
public static class KnowledgeSearchSorts
{
    public const string Relevance = "relevance";
    public const string Updated = "updated";
}

// FR-08, #1083, IADR-0454 決定2・3: 検索の絞り込みに使う文書属性のキー
// （情報収集の KnowledgeBaseWriterSink が保存時に載せる。銘柄を持たない文書には `symbol` が無い）。
public static class KnowledgeSearchAttributes
{
    public const string Symbol = "symbol";

    // FR-01, FR-02, FR-08, #1138, IADR-0474 決定1: 銘柄を持たない文書（市場全体のニュース・マクロ・収集状態・確定報告書）の目印。
    // 基盤の AttributeFilters は単値の完全一致だけで「属性が無い」を条件にできないため、保存時に肯定の値を書いて絞れるようにする。
    // 書き手は情報収集の KnowledgeBaseWriterSink（銘柄が無いときだけ）と報告書の ReportKnowledgeMapper（常に）の 2 か所。
    // キー名に `scope` を使わない（基盤の Scope〔ABAC の許可〕・doc_scope と紛れる）。
    public const string Coverage = "coverage";

    public const string MarketCoverage = "market";
}

// FR-08, #1300, IADR-0526 決定 1: 基盤の露出の 3 属性（MSP の FR-19・計画 ADR-0061。MSP の Knowledge.Contracts `DocumentExposure` と同じ文字列）。
// 3 つとも `excluded` の組織文書は、基盤の索引・検索・RAG・グラフ・Wiki・外部 AI エージェント向けの文書一覧に載らない（MSP#1886・MSP の IADR-0529）。
// SC-03 の閲覧（ABAC）は残る。AST は承認待ちの報告書の写し（ドラフト）にだけ付ける。
// 🔴 3 つのうち 1 つでも欠けると、その用途で検索・RAG に出る（基盤の判定は「1 つでも含めるなら索引する」）。
public static class KnowledgeExposureAttributes
{
    public const string SearchKey = "search_exposure";
    public const string GraphKey = "graph_exposure";
    public const string AiInputKey = "ai_input";
    public const string Excluded = "excluded";

    public static readonly IReadOnlyList<string> Keys = [SearchKey, GraphKey, AiInputKey];

    /// <summary>3 つとも <c>excluded</c> の属性か（基盤が索引しない文書か）。</summary>
    public static bool IsAllExcluded(IReadOnlyDictionary<string, string> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        return Keys.All(k => attributes.TryGetValue(k, out var v) && string.Equals(v, Excluded, StringComparison.Ordinal));
    }
}

// FR-06, FR-08, UC-03, #1300, IADR-0526 決定 1: 承認待ちの報告書の写し（ドラフト）の目印。書き手は報告書サービス、
// 読み手は報告書の入れ直し（確定版の写しと取り違えない）と取引判断の KB 検索（防御の絞り込み）。
// 表題は確定版（`確定報告書 {kind} {periodKey}`）と必ず分ける —— 入れ直しと MSP の写しの棚卸しは確定版の表題との完全一致を目印に使う。
public static class KnowledgeReportDraftCopy
{
    public const string TitlePrefix = "報告書ドラフト ";

    public const string StateKey = "reportState";
    public const string DraftState = "draft";

    public static bool IsDraftTitle(string? title) =>
        title is not null && title.StartsWith(TitlePrefix, StringComparison.Ordinal);
}

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
    DateTimeOffset? PublishedAt = null,
    // FR-08, #1083, IADR-0454 決定3: 文書属性 `symbol`（銘柄）。**銘柄を持たない文書（市場全体のニュース・マクロ・
    // 収集状態）は null**。判断側は目印（coverage）を持たない旧文書の補充（フィルタなしの検索。#1138, IADR-0474 決定3）から
    // 「銘柄を持たない文書」だけを残すのに使う。
    string? Symbol = null);

// FR-08, FR-11, #1283, IADR-0072 / IADR-0069: KB 検索の結果の状態。失敗は従来どおり空の結果に倒す（縮退は不変）が、
// 「失敗で空」と「検索は成功して 0 件」を呼び出し側が区別できるよう、状態を添えて返す。
public enum KnowledgeSearchOutcome
{
    /// <summary>検索は成功した（0 件を含む）。</summary>
    Succeeded,

    /// <summary>検索に失敗し、空の結果に倒した（非 2xx・例外・タイムアウト）。</summary>
    Failed,

    /// <summary>KB 検索が未構成（`KnowledgeBase:Search:BaseUrl` 未設定）で、検索していない。</summary>
    NotConfigured,
}

// FR-08, FR-11, #1283: KB 検索の結果と状態。
//   FailureCause — 失敗の原因の短い符号（`http-<状態コード>` / `timeout` / `exception:<型名>`）。
//                  🔴 本文・クエリ・URL・資格情報・例外のメッセージを入れない（ログへそのまま出すため）。
public sealed record KnowledgeSearchResult(
    IReadOnlyList<KnowledgeHit> Hits,
    KnowledgeSearchOutcome Outcome,
    string? FailureCause = null)
{
    public static KnowledgeSearchResult Succeeded(IReadOnlyList<KnowledgeHit> hits) => new(hits, KnowledgeSearchOutcome.Succeeded);

    public static KnowledgeSearchResult Failed(string cause) => new([], KnowledgeSearchOutcome.Failed, cause);

    public static KnowledgeSearchResult NotConfigured { get; } = new([], KnowledgeSearchOutcome.NotConfigured);
}
