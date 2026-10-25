using InformationCollectionService.Features.InformationCollection;
using Microsoft.Extensions.Logging;

namespace InformationCollectionService.Infrastructure.ExternalServices;

// FR-01, #1363, IADR-0547 決定 2: SEC EDGAR が 1 巡回で提出書類を読む CIK の集合。巡回ごとに決める。
public interface ISecEdgarCikSet
{
    Task<IReadOnlyList<string>> ResolveAsync(CancellationToken cancellationToken = default);
}

// 構成の固定リスト（Collection:Source:SecEdgar:Ciks）だけ。市場監視に結線していない組み立て（従来どおり）。
public sealed class FixedSecEdgarCikSet(IReadOnlyList<string> ciks) : ISecEdgarCikSet
{
    public Task<IReadOnlyList<string>> ResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(ciks);
}

// 🔴 FR-01, #1363, IADR-0547 決定 2・3: CIK を**収集の対象の銘柄（保有が先、続いて監視銘柄）から導く**。構成の固定リストは
// 「常に足す CIK」として併用する（銘柄に紐付かない提出者や、対応表に無い銘柄の CIK を足す口）。
//
// - 対応表（SecEdgarTickerCikDirectory）に無い銘柄（ETF・外国株の一部など）は飛ばし、件数と銘柄を情報ログに出す。
// - 🔴 対応表が不明（一度も読めていない）なら、銘柄からは導かず、固定リストだけで収集して警告する。固定リストも空なら
//   **例外を投げる**（SourceFetchRunner がこのソースを欠測として記録する。ADR-0020 決定 3 の「記録・通知のみ」）。
//   「対象 0 件で成功」と記録すると、開示を 1 件も集めていないことが欠測に現れない（原則 A: 不明は空ではない）。
public sealed class DerivedSecEdgarCikSet(
    ICollectionTargetSymbols targets,
    SecEdgarTickerCikDirectory directory,
    IReadOnlyList<string> configured,
    ILogger<DerivedSecEdgarCikSet> logger) : ISecEdgarCikSet
{
    public async Task<IReadOnlyList<string>> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var tickers = targets.UnitedStatesTargets;
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (tickers.Count > 0)
        {
            var table = await directory.GetAsync(cancellationToken).ConfigureAwait(false);
            if (table is null)
            {
                if (configured.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"SEC の ticker→CIK の対応表を一度も読めていないため、収集の対象の銘柄 {tickers.Count} 件から CIK を導けず、"
                        + "構成の固定 CIK（Collection:Source:SecEdgar:Ciks）も空です。SEC EDGAR を欠測として記録します。");
                }

                logger.LogWarning(
                    "SEC の ticker→CIK の対応表を一度も読めていないため、収集の対象の銘柄 {Count} 件から CIK を導けません。"
                    + "構成の固定 CIK {Configured} 件だけで収集します。",
                    tickers.Count, configured.Count);
            }
            else
            {
                var unmatched = new List<string>();
                foreach (var ticker in tickers)
                {
                    var cik = SecEdgarTickerCikDirectory.Lookup(table, ticker);
                    if (cik is null)
                        unmatched.Add(ticker);
                    else if (seen.Add(cik))
                        result.Add(cik);
                }

                logger.LogInformation(
                    "収集の対象の銘柄から SEC EDGAR の CIK を決めました: CIK {Count} 件（米国の銘柄 {Tickers} 件・対応表に無い {Unmatched} 件 {UnmatchedSymbols}）。",
                    result.Count, tickers.Count, unmatched.Count, string.Join(",", unmatched));
            }
        }

        foreach (var raw in configured)
        {
            var cik = SecEdgarInformationSource.NormalizeCik(raw);
            if (cik is null)
                result.Add(raw); // 数字でない値は従来どおり取得側が警告してスキップする。
            else if (seen.Add(cik))
                result.Add(cik);
        }

        return result;
    }
}
