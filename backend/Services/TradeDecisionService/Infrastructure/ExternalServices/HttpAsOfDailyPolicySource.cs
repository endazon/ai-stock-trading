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
                var path = "/reports/daily-policy/as-of?at=" + Uri.EscapeDataString(WireInstant(at));
                using var response = await httpClient.GetAsync(path, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return (true, null);
                if (!response.IsSuccessStatusCode)
                    return (false, null);

                var dto = await response.Content.ReadFromJsonAsync<PolicyDto>(ct).ConfigureAwait(false);
                return Interpret(dto?.Date, dto?.Summary, dto?.ConfirmedAt, dto?.PeriodKey, dto?.Version, at, logger, "as-of");
            },
            $"{at:O} の確定日報の方針",
            retryDelay ?? AsOfRetry.DefaultDelay,
            timeProvider,
            logger,
            cancellationToken);

    // NFR, #1388, IADR-0557 決定 4: 照会の時刻の線上表現（gRPC 実装と共有。秒まで・UTC の「Z」付き）。
    internal static string WireInstant(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // NFR, #1388, IADR-0557 決定 4: 応答の解釈（gRPC 実装 GrpcAsOfDailyPolicySource と共有する。輸送ごとに 2 つ書かない）。
    // 日付・要約・確定の時刻のどれかが欠けた応答は「取れなかった」（撃ち直す）。
    internal static (bool Ok, DailyPolicy? Value) Interpret(
        DateOnly? date, string? summary, DateTimeOffset? confirmedAt, string? periodKey, int? version,
        DateTimeOffset at, ILogger logger, string source)
    {
        if (date is not { } d || summary is null || confirmedAt is not { } confirmed)
            return (false, null);

        // 🔴 受け手でも確かめる: 照会の時刻より後に確定した方針は受け取らない（送り手の取り違えで未来の方針を入れない）。
        // 送り手の契約違反であり撃ち直しても変わらないので、撃ち直さずにすぐ記録を止める（#1326 の AI レビュー）。
        if (confirmed > at)
        {
            throw new AsOfInputUnavailableException(
                $"報告書サービスが照会の時刻 {at:O} より後（{confirmed:O}）に確定した方針を返しました（契約違反）。記録を止めます。");
        }

        // ADR-0068 決定3・4, #1373, IADR-0548: 判断時点までに確定した版の出所（欠けていれば null）。
        return (true, new DailyPolicy(d, summary, DailyPolicyReferences.From(periodKey, version, logger, source)));
    }

    private sealed record PolicyDto(
        DateOnly? Date, string? Summary, int? AssumptionsVersion, DateTimeOffset? ConfirmedAt,
        string? PeriodKey = null, int? Version = null);
}
