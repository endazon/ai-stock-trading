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
                if (dto is not { Date: { } date, Summary: { } summary, ConfirmedAt: { } confirmedAt })
                    return (false, null);
                // 🔴 受け手でも確かめる: 照会の時刻より後に確定した方針は受け取らない（送り手の取り違えで未来の方針を入れない）。
                // 送り手の契約違反であり撃ち直しても変わらないので、撃ち直さずにすぐ記録を止める（#1326 の AI レビュー）。
                if (confirmedAt > at)
                {
                    throw new AsOfInputUnavailableException(
                        $"報告書サービスが照会の時刻 {at:O} より後（{confirmedAt:O}）に確定した方針を返しました（契約違反）。記録を止めます。");
                }

                // ADR-0068 決定3・4, #1373, IADR-0548: 判断時点までに確定した版の出所（欠けていれば null）。
                return (true, new DailyPolicy(date, summary, DailyPolicyReferences.From(dto.PeriodKey, dto.Version, logger, "as-of")));
            },
            $"{at:O} の確定日報の方針",
            retryDelay ?? AsOfRetry.DefaultDelay,
            timeProvider,
            logger,
            cancellationToken);

    private sealed record PolicyDto(
        DateOnly? Date, string? Summary, int? AssumptionsVersion, DateTimeOffset? ConfirmedAt,
        string? PeriodKey = null, int? Version = null);
}
