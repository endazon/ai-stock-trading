using System.Globalization;
using System.Net.Http.Json;
using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.InformationCollection.Application.State;
using AiStockTrading.InformationCollection.Domain;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace AiStockTrading.InformationCollection.Infrastructure.Composable.Adapters;

// FR-01, ADR-0004/0005, IADR-0064: EDINET API v2（金融庁・日本の企業開示・無料／要 API キー）から当日提出の書類一覧を
// 取得する。レート制限は非公表のため 1 分 1 回程度に自制する（IRateLimiter）。取得失敗は空を返してログする
// （1 巡回を止めない）。実 API 前提の E2E は CI 対象外。
internal sealed class EdinetInformationSource(
    HttpClient httpClient,
    string subscriptionKey,
    IClock clock,
    IRateLimiter rateLimiter,
    ILogger<EdinetInformationSource> logger)
    : IInformationSource
{
    // EDINET は日本時間で運用され、日付パラメータ・提出時刻とも JST。日本標準時は夏時間を持たないため固定オフセットで扱う
    // （コンテナの tz データベースに依存しない）。
    private static readonly TimeSpan JapanOffset = TimeSpan.FromHours(9);

    public async Task<IReadOnlyList<RawInformationItem>> FetchAsync(CancellationToken cancellationToken = default)
    {
        var date = clock.UtcNow.ToOffset(JapanOffset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

        // type=2 は提出書類のメタデータ一覧。API キーはクエリ文字列で渡す仕様のため、OTel の HttpClient 計装が URL
        // （クエリ込み）をトレースへ出力してキーが漏えいするのを防ぐべく、この要求のみ計装を抑止する（IADR-0064）。
        var url = $"https://api.edinet-fsa.go.jp/api/v2/documents.json?date={date}&type=2" +
                  $"&Subscription-Key={Uri.EscapeDataString(subscriptionKey)}";

        EdinetDocuments? documents;
        using (SuppressInstrumentationScope.Begin())
        {
            using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "EDINET 取得失敗（{Date}）: {Status}。今回の巡回では EDINET を欠測として扱います。",
                    date, (int)response.StatusCode);
                return [];
            }

            documents = await response.Content
                .ReadFromJsonAsync<EdinetDocuments>(cancellationToken).ConfigureAwait(false);
        }

        if (documents?.Results is not { Count: > 0 } results)
            return [];

        return results.Select(Map).ToList();
    }

    private static RawInformationItem Map(EdinetDocument document) =>
        new(InformationKind.Disclosure,
            "edinet",
            ToTickerSymbol(document.SecCode),
            $"{document.FilerName} {document.DocDescription}",
            $"docId={document.DocID};filer={document.FilerName};description={document.DocDescription};" +
            $"docTypeCode={document.DocTypeCode};submitted={document.SubmitDateTime}",
            PublishedAt(document.SubmitDateTime),
            document.DocID is null ? null : $"https://api.edinet-fsa.go.jp/api/v2/documents/{document.DocID}");

    // EDINET の証券コードは 5 桁（末尾 0 埋め）。保有銘柄・他ソースと突き合わせるため 4 桁の銘柄コードへ寄せる。
    private static string? ToTickerSymbol(string? secCode) =>
        string.IsNullOrWhiteSpace(secCode) ? null
        : secCode.Length == 5 && secCode.EndsWith('0') ? secCode[..4]
        : secCode;

    // 提出時刻は "yyyy-MM-dd HH:mm"（JST）。読めなければ日付なしとして現在時刻に倒す。
    private static DateTimeOffset PublishedAt(string? submitDateTime)
    {
        if (DateTime.TryParseExact(
                submitDateTime, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return new DateTimeOffset(parsed, JapanOffset);

        return DateTimeOffset.UtcNow;
    }

    // EDINET API v2 書類一覧応答（必要な項目のみ）。
    private sealed record EdinetDocuments(IReadOnlyList<EdinetDocument>? Results);

    private sealed record EdinetDocument(
        string? DocID,
        string? SecCode,
        string? FilerName,
        string? DocDescription,
        string? SubmitDateTime,
        string? DocTypeCode);
}
