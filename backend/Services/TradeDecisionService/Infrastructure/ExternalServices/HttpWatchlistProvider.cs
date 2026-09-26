using System.Net.Http.Json;
using TradeDecisionService.Features.TradeDecision;
using Microsoft.Extensions.Logging;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-02, FR-13, UC-06, SC-02, IADR-0088/0095: 定時サイクルの監視銘柄を権威源（市場監視 #10 MarketMonitor）の
// GET /monitor/watchlist（OwnerOrService・IADR-0051）から s2s 同期照会する。SizingContext（IADR-0029）と同型の作法。
// 照会成功なら SC-02/API で変更された最新 watchlist を返し、以後の定時サイクルへ反映する。
// 供給不達（非 2xx・timeout・例外・不正応答）は fallback（構成ベース＝既定 watchlist・IADR-0095）へ委譲する fail-safe。
// FR-04, #1034, IADR-0440 決定 2: 判断のプロンプト用の口（GetAuthoritativeWatchlistAsync）は同じ照会を使い、
// 供給不達を fallback へ倒さず null（不明）で返す。
public sealed class HttpWatchlistProvider(
    HttpClient httpClient,
    IWatchlistProvider fallback,
    ILogger<HttpWatchlistProvider> logger)
    : IWatchlistProvider
{
    public async Task<IReadOnlyList<WatchedSymbol>> GetWatchlistAsync(CancellationToken cancellationToken = default)
    {
        var symbols = await TryFetchAsync(cancellationToken).ConfigureAwait(false);
        if (symbols is not null)
            return symbols;

        logger.LogWarning("監視銘柄（watchlist）を権威源から読めないため、既定 watchlist（構成）へフォールバックします。");
        return await fallback.GetWatchlistAsync(cancellationToken).ConfigureAwait(false);
    }

    // #1034, IADR-0440 決定 2: 読めなければ null（不明）。構成の固定リストへは倒さない。
    public Task<IReadOnlyList<WatchedSymbol>?> GetAuthoritativeWatchlistAsync(CancellationToken cancellationToken = default) =>
        TryFetchAsync(cancellationToken);

    // 権威源の照会。読めた一覧（空を含む）か、供給不達なら null。キャンセル（呼び出し側の停止要求）は伝播する。
    private async Task<IReadOnlyList<WatchedSymbol>?> TryFetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient
                .GetAsync("/monitor/watchlist", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("監視銘柄（watchlist）の照会に失敗（{Status}）。", (int)response.StatusCode);
                return null;
            }

            // MonitoredSymbol（MarketMonitorService.Domain）と WatchedSymbol は同形。camelCase・列挙は数値で往復する。
            var symbols = await response.Content
                .ReadFromJsonAsync<List<WatchedSymbol>>(cancellationToken)
                .ConfigureAwait(false);

            if (symbols is null)
            {
                logger.LogWarning("監視銘柄（watchlist）の応答が不正（null）。");
                return null;
            }

            return [.. symbols.Where(s => !string.IsNullOrWhiteSpace(s.Symbol))];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("監視銘柄（watchlist）の照会がタイムアウト。");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "監視銘柄（watchlist）の照会で例外。");
            return null;
        }
    }
}
