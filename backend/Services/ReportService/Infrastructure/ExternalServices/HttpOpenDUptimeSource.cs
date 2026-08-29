using System.Net.Http.Json;
using ReportService.Features.Reports;
using ReportService.Domain;
using Microsoft.Extensions.Logging;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-20, #569, INDEX 決定34, 04_report-templates 日報 §1 / 月報 §6.2, IADR-0271:
// 期間の OpenD 稼働率を権威源（リスク管理 #12 の稼働観測ログ）から s2s 同期照会する
// （GET /risk-controls/session-uptime・OwnerOrService・IADR-0051）。
//
// 🔴 **供給不達（未設定・非 2xx・timeout・例外・不正応答）はすべて `null`（未供給）へ倒す。**
// 同居する HttpPeriodFillSource は空列へ倒すが、あちらは「約定 0 件」が §1 サマリの取引回数 0 と
// 整合する事実だからである。**稼働率の 0% は「終日停止していた」という別の主張になる。揃えてはならない。**
//
// 🔴 **応答に現れない取引日を 0% として補完しない。** 観測窓の外・OpenD を経由しない発注先しか
// 観測が無い日は、権威源に行そのものが存在しない（`IStage1TradingDayObservationStore` の明文）。
public sealed class HttpOpenDUptimeSource(HttpClient httpClient, ILogger<HttpOpenDUptimeSource> logger)
    : IOpenDUptimeSource
{
    public async Task<OpenDUptimeRecord?> GetUptimeAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken = default)
    {
        var path = $"/risk-controls/session-uptime?from={fromInclusive:yyyy-MM-dd}&to={toInclusive:yyyy-MM-dd}";

        try
        {
            using var response = await httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "OpenD 稼働率の照会に失敗しました（{Status}・{From}〜{To}）。"
                        + "**未供給として扱います**（稼働率 0% とは書きません）。",
                    (int)response.StatusCode, fromInclusive, toInclusive);
                return null;
            }

            var view = await response.Content
                .ReadFromJsonAsync<SessionUptimeDto>(cancellationToken)
                .ConfigureAwait(false);

            if (view?.Days is null)
            {
                logger.LogWarning("OpenD 稼働率の応答が不正（null）でした。**未供給として扱います**。");
                return null;
            }

            return new OpenDUptimeRecord(
                [.. view.Days.Select(d => new OpenDUptimeDay(d.SessionDateEasternTime, d.UptimeRatio))],
                view.Stage1CumulativeCountedDays);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "OpenD 稼働率の照会がタイムアウトしました（{From}〜{To}）。**未供給として扱います**。",
                fromInclusive, toInclusive);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "OpenD 稼働率の照会で例外が発生しました（{From}〜{To}）。**未供給として扱います**。",
                fromInclusive, toInclusive);
            return null;
        }
    }

    // 権威源の SessionUptimeView と同形（camelCase で往復する）。
    private sealed record SessionUptimeDto(
        IReadOnlyList<SessionUptimeDayDto>? Days,
        int Stage1CumulativeCountedDays);

    private sealed record SessionUptimeDayDto(DateOnly SessionDateEasternTime, decimal UptimeRatio);
}
