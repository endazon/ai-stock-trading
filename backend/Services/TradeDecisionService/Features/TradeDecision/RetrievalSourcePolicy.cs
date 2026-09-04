using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Features.TradeDecision;

/// <summary>
/// FR-04, FR-08, ADR-0003, ADR-0004, #252, IADR-0169: 判断プロンプトへ注入してよい RAG 取得文脈を
/// <b>出典で限定する</b>（ADR-0003 が課したプロンプトインジェクション対策のうち「出典限定」）。
/// <para>
/// <b>新しい信頼の定義を発明しない。</b> 収集側は KB へ書くとき <c>Tags</c> に収集ソース名を載せており
/// （<c>KnowledgeBaseWriterSink</c>）、その語彙は収集側の <c>SourceAllowlist</c>（ADR-0004 の案A+）と
/// <b>同一である</b>。したがって本ポリシーは<b>既に決まっている「受理してよい情報源」を、注入側でも同じ語彙で効かせる</b>だけである。
/// </para>
/// <para>
/// <b>許可タグを 1 つも持たない文書は除外する（fail-closed）。</b> 「出所が分からないから通す」は統制にならない。
/// </para>
/// </summary>
public sealed class RetrievalSourcePolicy
{
    private readonly HashSet<string> _allowed;

    public RetrievalSourcePolicy(IEnumerable<string> allowedTags)
    {
        ArgumentNullException.ThrowIfNull(allowedTags);
        _allowed = new HashSet<string>(allowedTags, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 既定の許可出所 = <b>収集側の許可リスト（ADR-0004 案A+）∪ 自リポジトリが書いた文書種別</b>。
    /// <para>
    /// 収集ソース名は <c>InformationCollectionService.Domain.SourceAllowlist.Default</c> と同じ語彙である。
    /// <b>収集サービスの型を参照しない</b>のは、判断サービスが収集サービスへ依存しない構成（ユニット境界）を崩さないためである
    /// ——代わりに<b>語彙が一致していることをテストで固定する</b>（片方を増やしてもう片方を忘れる形を検知する）。
    /// </para>
    /// <para>
    /// <c>report</c> は自リポジトリの確定報告書（<c>ReportKnowledgeMapper</c> が付けるタグ）である。
    /// <b>自分で書いた文書は信頼する</b>——外部から注入された文字列ではない。
    /// </para>
    /// </summary>
    public static RetrievalSourcePolicy Default { get; } = new(
    [
        // --- 収集側の許可ソース（ADR-0004 案A+ / SourceAllowlist.Default と同一語彙） ---
        "finnhub",
        // #336, ADR-0020 決定2: ニュース系の 2 系統。**片方だけ足すと、その源の文書が判断へ一度も届かない。**
        "finnhub-news",
        "google-news",
        "sec-edgar",
        "edinet",
        "boj",
        "fred",
        // #687, ADR-0016 決定12: FINRA の空売り出来高（需給データ）。収集側 SourceAllowlist へ足した
        // のと**対で**足す —— 片方だけだと KB へは入るのに判断へは一度も届かない（本ファイル冒頭の
        // 「片方を増やしてもう片方を忘れる形」そのものであり、`RetrievalSourceVocabularyTests` が検知する）。
        "finra-short",
        "moomoo",
        // --- 自リポジトリが書いた文書 ---
        "report",
        // #336, ADR-0020 決定2-1: 収集サービス自身が書く「欠測している」ことの明示。
        // **外部から注入された文字列ではない**ため自リポジトリ文書として扱う。
        // これを落とすと、欠測が無言の空データとして判断へ渡る（ADR-0020 が塞いだ形が復活する）。
        "collection-status",
    ]);

    /// <summary>許可タグの集合（テスト・診断用。順序は保証しない）。</summary>
    public IReadOnlyCollection<string> AllowedTags => _allowed;

    /// <summary>
    /// 1 件が注入可能か。<b>許可タグを 1 つでも持てば可</b>（収集情報は「ソース名」＋「種別」＋「銘柄」を持つため、
    /// 全タグが許可語彙である必要はない）。タグが空・null は<b>不可</b>（出所が判定できない）。
    /// </summary>
    public bool IsAllowed(RetrievedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Tags.Any(t => !string.IsNullOrWhiteSpace(t) && _allowed.Contains(t));
    }

    /// <summary>
    /// 許可された文脈だけを返す。<b>順序は保つ</b>（関連度の並びを崩さない）。
    /// </summary>
    public IReadOnlyList<RetrievedContext> Filter(IReadOnlyList<RetrievedContext>? contexts)
    {
        if (contexts is null || contexts.Count == 0)
            return [];

        return contexts.Where(IsAllowed).ToList();
    }
}
