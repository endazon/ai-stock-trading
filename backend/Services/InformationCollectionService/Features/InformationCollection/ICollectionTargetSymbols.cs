namespace InformationCollectionService.Features.InformationCollection;

// FR-01, FR-02, #1363, IADR-0547 決定 2: その巡回の収集の対象（米国の銘柄。保有中の銘柄が先、続いて監視銘柄）を、
// Finnhub の 1 巡回の上限（ADR-0043 決定 2 (b)）を掛ける**前**の形で見せる。SEC EDGAR の CIK を銘柄から導くのに使う
// （SEC の上限は Finnhub の自制レートとは別のため、Finnhub の上限で削った後の集合を使わない）。
public interface ICollectionTargetSymbols
{
    IReadOnlyList<string> UnitedStatesTargets { get; }
}
