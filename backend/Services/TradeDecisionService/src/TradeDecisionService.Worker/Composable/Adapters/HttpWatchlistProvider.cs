using System.Net.Http.Json;
using AiStockTrading.TradeDecision.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TradeDecision.Worker.Composable.Adapters;

// FR-02, FR-13, UC-06, SC-02, IADR-0088/0095: 定時サイクルの監視銘柄を権威源（市場監視 #10 MarketMonitor）の
// GET /monitor/watchlist（OwnerOrService・IADR-0051）から s2s 同期照会する。SizingContext（IADR-0029）と同型の作法。
// 照会成功なら SC-02/API で変更された最新 watchlist を返し、以後の定時サイクルへ反映する。
// 供給不達（非 2xx・timeout・例外・不正応答）は fallback（構成ベース＝既定 watchlist・IADR-0095）へ委譲する fail-safe。
internal sealed class HttpWatchlistProvider(
    HttpClient httpClient,
    IWatchlistProvider fallback,
    ILogger<HttpWatchlistProvider> logger)
    : IWatchlistProvider
{
    public async Task<IReadOnlyList<WatchedSymbol>> GetWatchlistAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient
                .GetAsync("/monitor/watchlist", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "監視銘柄（watchlist）の照会に失敗（{Status}）。既定 watchlist（構成）へフォールバックします。",
                    (int)response.StatusCode);
                return await fallback.GetWatchlistAsync(cancellationToken).ConfigureAwait(false);
            }

            // MonitoredSymbol（MarketMonitor.Domain）と WatchedSymbol は同形。camelCase・列挙は数値で往復する。
            var symbols = await response.Content
                .ReadFromJsonAsync<List<WatchedSymbol>>(cancellationToken)
                .ConfigureAwait(false);

            if (symbols is null)
            {
                logger.LogWarning("監視銘柄（watchlist）の応答が不正（null）。既定 watchlist（構成）へフォールバックします。");
                return await fallback.GetWatchlistAsync(cancellationToken).ConfigureAwait(false);
            }

            return [.. symbols.Where(s => !string.IsNullOrWhiteSpace(s.Symbol))];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("監視銘柄（watchlist）の照会がタイムアウト。既定 watchlist（構成）へフォールバックします。");
            return await fallback.GetWatchlistAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "監視銘柄（watchlist）の照会で例外。既定 watchlist（構成）へフォールバックします。");
            return await fallback.GetWatchlistAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
