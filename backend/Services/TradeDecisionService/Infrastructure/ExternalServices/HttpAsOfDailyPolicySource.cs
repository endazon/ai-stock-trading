using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-15, FR-07, #1308, IADR-0528 決定3: 時刻 at の時点で確定していた日報の方針（報告書サービス `GET /reports/daily-policy/as-of`）。
// 404 は「確定日報が無かった」（null）。それ以外の失敗は撃ち直してから例外（記録を止める）。
public sealed class HttpAsOfDailyPolicySource(
    HttpClient httpClient,
    TimeProvider timeProvider,
    ILogger<HttpAsOfDailyPolicySource> logger,
    TimeSpan? retryDelay = null)
    : IAsOfDailyPolicySource
{
    public Task<DailyPolicy?> GetAsync(DateTimeOffset at, CancellationToken cancellationToken = default) =>
        AsOfRetry.RunAsync<DailyPolicy?>(
            async ct =>
            {
                var path = "/reports/daily-policy/as-of?at="
                    + Uri.EscapeDataString(at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                using var response = await httpClient.GetAsync(path, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return (true, null);
                if (!response.IsSuccessStatusCode)
                    return (false, null);

                var dto = await response.Content.ReadFromJsonAsync<PolicyDto>(ct).ConfigureAwait(false);
                // 🔴 受け手でも確かめる: 照会の時刻より後に確定した方針は受け取らない（送り手の取り違えで未来の方針を入れない）。
                if (dto is not { Date: { } date, Summary: { } summary, ConfirmedAt: { } confirmedAt } || confirmedAt > at)
                    return (false, null);
                return (true, new DailyPolicy(date, summary));
            },
            $"{at:O} の確定日報の方針",
            retryDelay ?? AsOfRetry.DefaultDelay,
            timeProvider,
            logger,
            cancellationToken);

    private sealed record PolicyDto(DateOnly? Date, string? Summary, int? AssumptionsVersion, DateTimeOffset? ConfirmedAt);
}
