using System.Net;
using System.Net.Http.Json;
using AiStockTrading.TradeDecision.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TradeDecision.Worker.Composable.Adapters;

// FR-04, FR-07, IADR-0028: 確定済み日報方針を報告書サービス（#14）の GET /reports/daily-policy から同期照会する。
// 未確定（404）・非 2xx・例外・不達は null（＝取引しない）に倒す（フェイルセーフ・FR-07 の安全既定と一致）。
internal sealed class HttpDailyPolicyProvider(
    HttpClient httpClient,
    ILogger<HttpDailyPolicyProvider> logger)
    : IDailyPolicyProvider
{
    public async Task<DailyPolicy?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient
                .GetAsync("/reports/daily-policy", cancellationToken)
                .ConfigureAwait(false);

            // 未確定は 404。それ以外の失敗（認証・障害等）も取引しない安全側に倒す。
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("確定済み日報方針の照会に失敗（{Status}）。取引しない安全側に倒します。", (int)response.StatusCode);
                return null;
            }

            var dto = await response.Content.ReadFromJsonAsync<ConfirmedDailyPolicyDto>(cancellationToken).ConfigureAwait(false);
            return dto is null ? null : new DailyPolicy(dto.Date, dto.Summary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 報告書サービス不達・タイムアウト等は取引しない安全側に倒す。
            logger.LogWarning(ex, "確定済み日報方針の照会で例外。取引しない安全側に倒します。");
            return null;
        }
    }

    // GET /reports/daily-policy の応答（ConfirmedDailyPolicy）。AssumptionsVersion は本結線では未使用。
    private sealed record ConfirmedDailyPolicyDto(DateOnly Date, string Summary, int AssumptionsVersion);
}
