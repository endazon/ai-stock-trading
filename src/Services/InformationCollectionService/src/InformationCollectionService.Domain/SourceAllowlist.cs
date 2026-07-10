namespace AiStockTrading.InformationCollection.Domain;

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
        "sec-edgar",
        "edinet",
        "boj",
        "fred",
        "moomoo",
    });
}
