namespace AiStockTrading.Shared.Contracts.Trading;

// FR-06, FR-08, FR-11, #1356, IADR-0541 決定 1: 判断のプロンプトが**実際に見た**参考情報（RAG）の記録。
// 日報 §5「判断に影響したニュース・開示（出典リンク）」の供給元である（報告書は監査台帳からこの記録を引く）。
//
// 🔴 **本文抜粋は載せない**（記録の量と、外部の自由文が下流のプロンプトへ届く面を増やさない）。表題・出典 URI・発行時刻・出所タグだけ。
// RetrievalState は取引判断のログの ragContext と同じ語彙（retrieved / empty / failed / not-configured と、縮退の印 -degraded）。
// Items は「取得できたが空」なら空列。**記録そのものが無い（null）**のは本列の追加前の判断であり、報告書は「参照の記録なし」と描く。
public sealed record DecisionReferences(string RetrievalState, IReadOnlyList<DecisionReference> Items)
{
    /// <summary>表題の上限（文字数）。超えた分は切る（記録の量を抑える）。</summary>
    public const int MaxTitleLength = 300;

    /// <summary>出典 URI の上限（文字数）。超えた URI は記録しない（null）。</summary>
    public const int MaxSourceUriLength = 2048;

    /// <summary>1 判断で記録する件数の上限。</summary>
    public const int MaxItems = 50;
}

// FR-06, FR-08, #1356, IADR-0541 決定 1: 参考情報 1 件（出典の識別に要る項目だけ）。SourceUri・PublishedAt は無ければ null。
public sealed record DecisionReference(
    string Title,
    string? SourceUri,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<string>? Tags = null);
