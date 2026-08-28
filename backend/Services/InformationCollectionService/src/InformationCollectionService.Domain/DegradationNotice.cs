namespace InformationCollectionService.Domain;

// FR-01, FR-08, ADR-0020 決定2-1: 「ニュース情報が欠測している」ことを**取引判断の文脈へ明示して渡す**。
//
// 🔴 **欠測を無言で空データとして渡さない。** 何も書かないと、判断側からは「今日はニュースが無かった」と
// 区別がつかない —— **取れなかったのか、無かったのか**が分かれないまま LLM の文脈に入る。
public static class DegradationNotice
{
    /// <summary>
    /// 収集状態ドキュメントの出所名。**外部の情報源ではなく本サービスが書いた文書**であり、
    /// RAG 注入側（TradeDecision の RetrievalSourcePolicy）でも自リポジトリ文書として許可する。
    /// </summary>
    public const string SourceName = "collection-status";

    /// <summary>
    /// 縮退の内容を述べる収集情報を作る。<b>本文は収集サービスが書き起こす</b>ため、
    /// 外部テキストのサニタイズ（PromptSafetySanitizer）は経路の共通処理としてのみ通す。
    /// </summary>
    public static CollectedInformation Create(CollectionDegradation degradation, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(degradation);

        var lines = new List<string>();

        if (degradation.NewsOutage)
        {
            lines.Add(
                "ニュース情報は欠測している（Finnhub 企業ニュース・Google News RSS のいずれも取得できていない）。"
                + "この状態では単一ソース由来の急シグナルの裏取りが構造的に取れないため、"
                + "急シグナルに基づく新規建てを行わないこと。");
        }

        lines.AddRange(degradation.Notifications);

        // 🔴 **止まっていないものを必ず書く。** 「縮退」とだけ伝えると、判断側が出口まで塞がっていると読み得る。
        lines.Add("手仕舞い（Close）と損切りは止まっていない。");

        return new CollectedInformation(
            InformationKind.SourceStatus,
            SourceName,
            Symbol: null,
            Title: "情報収集の縮退状態",
            Content: string.Join("\n", lines),
            PublishedAt: occurredAt,
            Url: null);
    }
}
