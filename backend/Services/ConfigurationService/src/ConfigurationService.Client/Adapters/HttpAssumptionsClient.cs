using System.Net.Http.Json;
using AiStockTrading.Configuration.Client.Ports;
using AiStockTrading.Configuration.Domain;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Configuration.Client.Adapters;

// FR-17, IADR-0063 決定 1: 設定サービス（#19）の GET /assumptions を同期照会して現在の前提条件と Version を得る。
// 非 2xx・例外・タイムアウト・不正応答は null（＝取得不可）に倒し、例外を伝播させない。何へ縮退するかの判断は
// CachedAssumptionsProvider（決定 5: last known good ＞ 既定値）が持つ。
// 認証（IADR-0051/0063 決定 2）: /assumptions の GET は OwnerOrService のため、HttpClient に付与された
// client_credentials サービストークン（trading-service）で認証される（AddAiStockTradingServiceToken）。
// ServiceAuth:ClientId/ClientSecret 未設定ならヘッダなし＝401 → 取得不可（既定へ縮退）。
internal sealed class HttpAssumptionsClient(
    HttpClient httpClient,
    ILogger<HttpAssumptionsClient> logger)
    : IAssumptionsSource
{
    public async Task<VersionedAssumptions?> FetchAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient
                .GetAsync("/assumptions", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("全体前提条件の照会に失敗（{Status}）。既知の値または既定へ倒します。", (int)response.StatusCode);
                return null;
            }

            var dto = await response.Content
                .ReadFromJsonAsync<VersionedAssumptions>(cancellationToken)
                .ConfigureAwait(false);

            // 版が付いていない応答は解決済みとみなせない（番兵 0 と衝突する）ため取得不可として扱う。
            if (dto is null || !dto.IsResolved)
            {
                logger.LogWarning("全体前提条件の応答が不正（版なし）。既知の値または既定へ倒します。");
                return null;
            }

            return dto;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("全体前提条件の照会がタイムアウト。既知の値または既定へ倒します。");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "全体前提条件の照会で例外。既知の値または既定へ倒します。");
            return null;
        }
    }
}
