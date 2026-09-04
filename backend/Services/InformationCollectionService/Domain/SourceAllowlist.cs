namespace InformationCollectionService.Domain;

// FR-01, ADR-0004: 情報源の許可リスト（案A+ の公式/準公式ソースのみ受理）。非許可ソースのアイテムは破棄する
// （ニュース入力の防御・許可リストに限定）。ソース名は大文字小文字を無視して照合する。
public sealed class SourceAllowlist
{
    private readonly HashSet<string> _allowed;

    public SourceAllowlist(IEnumerable<string> allowedSources)
    {
        ArgumentNullException.ThrowIfNull(allowedSources);
        _allowed = new HashSet<string>(allowedSources, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsAllowed(string? source) =>
        !string.IsNullOrWhiteSpace(source) && _allowed.Contains(source);

    // 案A+（ADR-0004）: 公式重視・moomoo中核・米国株強化=Finnhub併用。マクロは BOJ/FRED、開示は EDINET/SEC EDGAR。
    public static SourceAllowlist Default { get; } = new(new[]
    {
        "finnhub",
        // ADR-0020 決定2: ニュース系（いずれか 1 つ以上が生きていることを必須とする 2 系統）。
        "finnhub-news",
        "google-news",
        "sec-edgar",
        "edinet",
        "boj",
        "fred",
        "moomoo",
        // FR-01, ADR-0016 決定12, #687: FINRA 空売りデータ（需給・米国株のみ必須）。
        "finra-short",
    });
}
