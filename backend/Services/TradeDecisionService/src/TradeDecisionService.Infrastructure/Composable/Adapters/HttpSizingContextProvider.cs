using System.Net.Http.Json;
using RiskManagementService.Domain;
using TradeDecisionService.Application.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace TradeDecisionService.Infrastructure.Adapters;

// FR-04, FR-10, IADR-0029: サイジング文脈をリスク管理（#12）の GET /risk-controls/sizing-context から同期照会する。
// 未取得・非 2xx・例外・タイムアウト・不正応答は残枠 0 の安全既定（availableCapital 0 → 数量 0 → 見送り＝取引しない）に倒す。
internal sealed class HttpSizingContextProvider(
    HttpClient httpClient,
    ILogger<HttpSizingContextProvider> logger)
    : ISizingContextProvider
{
    public async Task<SizingContext> GetContextAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient
                .GetAsync("/risk-controls/sizing-context", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("サイジング文脈の照会に失敗（{Status}）。残枠 0 の安全既定に倒します。", (int)response.StatusCode);
                return SafeDefault();
            }

            // SizingContextView（RiskManagement）と SizingContext は同形。camelCase・列挙は数値で往復する。
            var context = await response.Content.ReadFromJsonAsync<SizingContext>(cancellationToken).ConfigureAwait(false);
            return context ?? SafeDefault();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("サイジング文脈の照会がタイムアウト。残枠 0 の安全既定に倒します。");
            return SafeDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "サイジング文脈の照会で例外。残枠 0 の安全既定に倒します。");
            return SafeDefault();
        }
    }

    // フェイルセーフ既定: 段階/日次残枠 0 → availableCapital 0 → 数量 0 → 見送り（取引しない）。Limits は PositionSizer を動かせる既定値。
    private static SizingContext SafeDefault() => new(
        Capital: TradingDefaults.InitialCapital,
        StageCapitalRemaining: 0m,
        DailyOrderRemaining: 0m,
        ConsecutiveLosses: 0,
        DrawdownRatio: 0m,
        Mode: BrokerProvider.InternalPaper,
        Limits: TradingDefaults.CreateRiskLimits());
}
