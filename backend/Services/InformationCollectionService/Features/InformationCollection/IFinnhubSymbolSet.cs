namespace InformationCollectionService.Features.InformationCollection;

// FR-01, #1015, IADR-0435: Finnhub の情報源（現在値・企業ニュース）が 1 巡回で問い合わせる銘柄の集合。
// 集合は巡回の最初に 1 回だけ決まり（FinnhubSymbolSelector.RefreshAsync）、同じ巡回の 2 つのソースは同じ集合を見る。
public interface IFinnhubSymbolSet
{
    IReadOnlyList<string> Current { get; }
}

// 固定の集合（構成の Collection:Source:Finnhub:Symbols をそのまま使う。単体の試験と、選択器を介さない組み立て用）。
public sealed class FixedFinnhubSymbolSet(IReadOnlyList<string> symbols) : IFinnhubSymbolSet
{
    public IReadOnlyList<string> Current => symbols;
}
