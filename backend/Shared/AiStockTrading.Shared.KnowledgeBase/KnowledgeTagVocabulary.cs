namespace AiStockTrading.Shared.KnowledgeBase;

// FR-01, FR-08, IADR-0315, #705: AST が KB（platform document-service）へ送り得るタグの静的語彙を
// 1 か所へ集約した単一情報源。
//
// 背景: platform document-service の POST /documents は MSP#635 でタグ辞書検証（未登録タグは 400）
// を持つに至った。AST 側でタグに載せてよいのは**事前登録できる静的な語彙だけ**である
// （監視銘柄コードのように運用中に増える動的集合は載せない。KnowledgeBaseWriterSink 参照）。
//
// 本クラスは**実行時の検証・フィルタには使わない**（過剰な抽象化をしない。#705 の目的は「基盤の辞書へ
// 登録すべきタグ一覧を人・運用が機械的に取り出せる」ことに限る。運用手順は docs/operations/ の Runbook）。
//
// 🔴 値は各サービス側の実体（InformationCollectionService.Domain.InformationKind の各値・
// InformationCollectionService.Domain.SourceAllowlist.Default・ReportService.Domain.ReportKind の各値と
// ReportKnowledgeMapper が付ける "report"）の**複製**である。本プロジェクト（Shared）は Services へ
// 依存できない（依存方向は Services → Shared。IADR-0256）ため型を直接参照できず、文字列リテラルとして
// 複製するほかない。複製が実体と食い違わないことは `KnowledgeTagVocabularyTests`
// （AiStockTrading.Architecture.Tests・ソース静的解析。`RetrievalSourceVocabularyTests` と同じ作法）が
// 機械的に固定する——**このテストを消さないこと**。
public static class KnowledgeTagVocabulary
{
    /// <summary>
    /// InformationCollectionService.Domain.InformationKind の各値の既定 <c>ToString()</c>
    /// （<c>KnowledgeBaseWriterSink.ToDocument</c> が Tags へ載せる形と同一）。
    /// </summary>
    public static readonly IReadOnlyList<string> CollectionKinds =
    [
        "Quote",
        "News",
        "Disclosure",
        "MacroIndicator",
        "SupplyDemand",
        "SourceStatus",
    ];

    /// <summary>
    /// InformationCollectionService.Domain.SourceAllowlist.Default と同一語彙（ADR-0004 案A+）。
    /// </summary>
    public static readonly IReadOnlyList<string> CollectionSources =
    [
        "finnhub",
        "finnhub-news",
        "google-news",
        "sec-edgar",
        "edinet",
        "boj",
        "fred",
        "moomoo",
        "finra-short",
    ];

    /// <summary>
    /// ReportService.Infrastructure.ExternalServices.ReportKnowledgeMapper が付ける静的タグ
    /// （<c>"report"</c> ＋ ReportService.Domain.ReportKind の各値を小文字化したもの）。
    /// </summary>
    public static readonly IReadOnlyList<string> ReportTags =
    [
        "report",
        "daily",
        "weekly",
        "monthly",
    ];

    /// <summary>
    /// 上記 3 群の和集合（基盤の辞書へ登録すべきタグの全集合）。順序は保証しない。
    /// </summary>
    public static IReadOnlyCollection<string> All { get; } =
        CollectionKinds
            .Concat(CollectionSources)
            .Concat(ReportTags)
            .ToHashSet(StringComparer.Ordinal);
}
