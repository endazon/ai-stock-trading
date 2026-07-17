using System.Globalization;
using System.Net.Http.Json;
using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.InformationCollection.Application.State;
using AiStockTrading.InformationCollection.Domain;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.InformationCollection.Worker.Composable.Adapters;

// FR-01, ADR-0004/0005, IADR-0064: SEC EDGAR（米国の企業開示・無料・APIキー不要）から直近の提出書類を取得する。
// 規約上 User-Agent に連絡先が必須のため、未設定なら本ソースは有効化しない（InformationSourceFactory で除外）。
// レート上限は 10 回/秒/IP のため送信前に自制する（IRateLimiter）。取得失敗（レート制限・一時エラー）は当該 CIK を
// スキップしてログする（1 巡回を止めない）。実 API 前提の E2E は CI 対象外。
internal sealed class SecEdgarInformationSource(
    HttpClient httpClient,
    string userAgent,
    IReadOnlyList<string> ciks,
    IRateLimiter rateLimiter,
    ILogger<SecEdgarInformationSource> logger)
    : IInformationSource
{
    // 1 CIK あたり読み取る直近の提出件数（1 巡回で LLM 文脈に載せられる量に抑える）。
    private const int RecentLimit = 10;

    public async Task<IReadOnlyList<RawInformationItem>> FetchAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<RawInformationItem>();

        foreach (var cik in ciks)
        {
            var normalized = NormalizeCik(cik);
            if (normalized is null)
            {
                logger.LogWarning("SEC EDGAR の CIK '{Cik}' は数字ではないためスキップします。", cik);
                continue;
            }

            await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"https://data.sec.gov/submissions/CIK{normalized}.json");
            // SEC は連絡先入りの User-Agent を必須としている（未申告のアクセスはブロックされる）。
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "SEC EDGAR 取得失敗（CIK {Cik}）: {Status}。この CIK をスキップします。", normalized, (int)response.StatusCode);
                continue;
            }

            var submissions = await response.Content
                .ReadFromJsonAsync<Submissions>(cancellationToken).ConfigureAwait(false);
            if (submissions?.Filings?.Recent is not { } recent)
                continue;

            items.AddRange(MapFilings(submissions, recent, normalized));
        }

        return items;
    }

    private static IEnumerable<RawInformationItem> MapFilings(Submissions submissions, Recent recent, string cik)
    {
        var company = submissions.Name ?? cik;
        var symbol = submissions.Tickers?.FirstOrDefault();
        var count = Math.Min(RecentLimit, recent.AccessionNumber?.Count ?? 0);

        for (var i = 0; i < count; i++)
        {
            var form = At(recent.Form, i) ?? "";
            var accession = At(recent.AccessionNumber, i) ?? "";
            var filingDate = At(recent.FilingDate, i) ?? "";
            var reportDate = At(recent.ReportDate, i) ?? "";
            var description = At(recent.PrimaryDocDescription, i) ?? "";

            yield return new RawInformationItem(
                InformationKind.Disclosure,
                "sec-edgar",
                symbol,
                $"{company} {form}",
                $"form={form};filed={filingDate};reportDate={reportDate};description={description}",
                PublishedAt(At(recent.AcceptanceDateTime, i), filingDate),
                DocumentUrl(cik, accession, At(recent.PrimaryDocument, i)));
        }
    }

    // 提出受理時刻（acceptanceDateTime）を優先し、無ければ提出日（filingDate）。いずれも読めなければ現在時刻。
    private static DateTimeOffset PublishedAt(string? acceptance, string filingDate)
    {
        if (DateTimeOffset.TryParse(
                acceptance, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var accepted))
            return accepted.ToUniversalTime();

        if (DateTimeOffset.TryParse(
                filingDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var filed))
            return filed.ToUniversalTime();

        return DateTimeOffset.UtcNow;
    }

    // 一次資料（提出書類そのもの）へ辿れる URL。EDGAR のアーカイブは CIK（ゼロ埋めなし）＋ハイフン無しの受理番号で構成される。
    private static string? DocumentUrl(string cik, string accession, string? primaryDocument)
    {
        if (string.IsNullOrWhiteSpace(accession) || string.IsNullOrWhiteSpace(primaryDocument))
            return null;

        return $"https://www.sec.gov/Archives/edgar/data/{cik.TrimStart('0')}/{accession.Replace("-", "")}/{primaryDocument}";
    }

    private static string? At(IReadOnlyList<string>? values, int index) =>
        values is not null && index < values.Count ? values[index] : null;

    // "320193" / "CIK0000320193" のどちらでも 10 桁ゼロ埋めへ正規化する。数字が無ければ null（スキップ）。
    private static string? NormalizeCik(string cik)
    {
        var digits = new string(cik.Where(char.IsAsciiDigit).ToArray());
        return digits.Length is 0 or > 10 ? null : digits.PadLeft(10, '0');
    }

    // data.sec.gov/submissions 応答（必要な項目のみ）。filings.recent は列指向（同一 index が 1 件の提出に対応）。
    private sealed record Submissions(string? Name, IReadOnlyList<string>? Tickers, Filings? Filings);

    private sealed record Filings(Recent? Recent);

    private sealed record Recent(
        IReadOnlyList<string>? AccessionNumber,
        IReadOnlyList<string>? FilingDate,
        IReadOnlyList<string>? ReportDate,
        IReadOnlyList<string>? AcceptanceDateTime,
        IReadOnlyList<string>? Form,
        IReadOnlyList<string>? PrimaryDocument,
        IReadOnlyList<string>? PrimaryDocDescription);
}
