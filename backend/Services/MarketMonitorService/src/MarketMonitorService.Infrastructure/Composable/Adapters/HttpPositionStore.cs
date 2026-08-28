using System.Net.Http.Json;
using MarketMonitorService.Application.Ports;
using MarketMonitorService.Domain;
using Microsoft.Extensions.Logging;

namespace MarketMonitorService.Infrastructure.Adapters;

// FR-03, FR-10, IADR-0030: 保有ポジションをリスク管理（#12・#63 台帳）の GET /risk-controls/open-positions から同期照会する。
// 未取得・非 2xx・例外・タイムアウト・不正応答は空列の安全既定（＝損切り検知対象なし）に倒す。
// 注意: 空列は「取引しない」と異なり損切り保護が働かない側の縮退だが、保有情報なしに損切り価格は知り得ないため
// 依存先障害時に取り得る唯一の縮退（IADR-0030・既存 PlaceholderPositionStore と同一既定）。
internal sealed class HttpPositionStore(
    HttpClient httpClient,
    ILogger<HttpPositionStore> logger)
    : IPositionStore
{
    private static readonly IReadOnlyCollection<HeldPosition> Empty = [];

    public async Task<IReadOnlyCollection<HeldPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient
                .GetAsync("/risk-controls/open-positions", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("保有ポジションの照会に失敗（{Status}）。空列（損切り検知対象なし）に倒します。", (int)response.StatusCode);
                return Empty;
            }

            // OpenPositionView（RiskManagement）と HeldPosition は同形。camelCase・列挙は数値で往復する。
            var positions = await response.Content
                .ReadFromJsonAsync<List<HeldPosition>>(cancellationToken)
                .ConfigureAwait(false);
            return positions is null ? Empty : positions;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("保有ポジションの照会がタイムアウト。空列（損切り検知対象なし）に倒します。");
            return Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "保有ポジションの照会で例外。空列（損切り検知対象なし）に倒します。");
            return Empty;
        }
    }
}
