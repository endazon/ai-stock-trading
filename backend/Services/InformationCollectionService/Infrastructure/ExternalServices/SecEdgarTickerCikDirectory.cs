using System.Text.Json;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using Microsoft.Extensions.Logging;

namespace InformationCollectionService.Infrastructure.ExternalServices;

// 🔴 FR-01, #1363, IADR-0547 決定 2: SEC が公開する ticker→CIK の対応表（https://www.sec.gov/files/company_tickers.json）を読み、
// 銘柄コードから CIK を引く。表は SEC が日次で更新するため、**読めてから 24 時間は読み直さない**（1 日 1 要求）。
//
// - 送信の規律は提出書類の取得と同じ: 連絡先入りの User-Agent（SEC 規約・sec-edgar-user-agent）、同じ自制のバケット（10 回/秒/IP の内側）。
// - 🔴 **原則 A: 不明は空ではない。** 読めなければ直前に読めた表を使い続け（警告し、1 時間後に読み直す）、一度も読めていなければ
//   null（不明）を返す（次の巡回で読み直す）。0 件の表・解釈できない本文も「読めなかった」とする（直前の表を空で上書きしない）。
public sealed class SecEdgarTickerCikDirectory(
    HttpClient httpClient,
    string userAgent,
    IRateLimiter rateLimiter,
    TimeProvider timeProvider,
    ILogger<SecEdgarTickerCikDirectory> logger)
{
    public const string CompanyTickersUrl = "https://www.sec.gov/files/company_tickers.json";

    /// <summary>読めた表を読み直すまでの間隔（SEC は表を日次で更新する）。</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);

    /// <summary>直前の表があるときに、読めなかった後で読み直すまでの間隔（失敗のたびに巡回ごとに叩かない）。</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<string, string>? _table;
    private DateTimeOffset _nextAttemptAt = DateTimeOffset.MinValue;

    /// <summary>ticker（大文字）→ 10 桁ゼロ埋めの CIK。一度も読めていなければ <c>null</c>（不明）。</summary>
    public async Task<IReadOnlyDictionary<string, string>?> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = timeProvider.GetUtcNow();
            if (now < _nextAttemptAt)
                return _table;

            var loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (loaded is not null)
            {
                _table = loaded;
                _nextAttemptAt = now + RefreshInterval;
                logger.LogInformation("SEC の ticker→CIK の対応表を読みました: {Count} 件。", loaded.Count);
            }
            else if (_table is not null)
            {
                _nextAttemptAt = now + RetryInterval;
                logger.LogWarning(
                    "SEC の ticker→CIK の対応表を読み直せなかったため、直前に読めた表（{Count} 件）を使い続けます"
                    + "（{Retry} 後に読み直します）。", _table.Count, RetryInterval);
            }

            return _table;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 銘柄コードから CIK を引く。SEC の表はクラス株を <c>BRK-B</c> の形で持つため、<c>BRK.B</c> も引けるようにする。
    /// </summary>
    public static string? Lookup(IReadOnlyDictionary<string, string> table, string ticker)
    {
        var key = ticker.Trim().ToUpperInvariant();
        if (table.TryGetValue(key, out var cik))
            return cik;

        return key.Contains('.', StringComparison.Ordinal) && table.TryGetValue(key.Replace('.', '-'), out cik) ? cik : null;
    }

    private async Task<IReadOnlyDictionary<string, string>?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, CompanyTickersUrl);
            // SEC は連絡先入りの User-Agent を必須としている（未申告のアクセスはブロックされる）。
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("SEC の ticker→CIK の対応表の取得に失敗しました（{Status}）。", (int)response.StatusCode);
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            var table = Parse(document.RootElement);
            if (table is null || table.Count == 0)
            {
                logger.LogWarning("SEC の ticker→CIK の対応表を解釈できないか 0 件でした。読めなかったものとして扱います。");
                return null;
            }

            return table;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SEC の ticker→CIK の対応表の取得で例外が発生しました。");
            return null;
        }
    }

    // company_tickers.json は {"0":{"cik_str":320193,"ticker":"AAPL","title":"Apple Inc."}, ...} の形。
    // 項目の欠けた行は飛ばす（表全体を捨てると 1 行の欠けで全銘柄が引けなくなる。引けない銘柄は呼び出し側が記録する）。
    // 同じ ticker が重なれば最初の行を採る。
    internal static Dictionary<string, string>? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object
                || !entry.Value.TryGetProperty("ticker", out var tickerElement)
                || tickerElement.ValueKind != JsonValueKind.String
                || !entry.Value.TryGetProperty("cik_str", out var cikElement))
                continue;

            var ticker = tickerElement.GetString()?.Trim().ToUpperInvariant();
            var cik = cikElement.ValueKind switch
            {
                JsonValueKind.Number when cikElement.TryGetInt64(out var n) && n > 0 => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                JsonValueKind.String => cikElement.GetString(),
                _ => null,
            };
            var normalized = SecEdgarInformationSource.NormalizeCik(cik ?? "");
            if (string.IsNullOrEmpty(ticker) || normalized is null)
                continue;

            table.TryAdd(ticker, normalized);
        }

        return table;
    }
}
