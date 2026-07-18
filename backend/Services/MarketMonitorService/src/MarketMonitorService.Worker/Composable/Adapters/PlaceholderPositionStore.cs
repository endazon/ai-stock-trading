using AiStockTrading.MarketMonitor.Application.Ports;
using AiStockTrading.MarketMonitor.Domain;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.MarketMonitor.Worker.Composable.Adapters;

// FR-03, FR-10: IPositionStore のフォールバック実装。保有・損切り価格の実データはリスク管理（#12/#63 台帳）を照会する
// HttpPositionStore（RiskManagement:BaseUrl）が供給する。BaseUrl 未設定時は本実装が「保有なし」を返す（＝損切り検知対象なし）。差し替え漏れ検知のため初回利用時に 1 回警告する
// （#12 の PlaceholderPortfolioStateProvider を踏襲。singleton 登録・GetOpenPositions は毎巡回呼ばれるためログ氾濫を避ける）。
internal sealed class PlaceholderPositionStore(ILogger<PlaceholderPositionStore> logger) : IPositionStore
{
    private int _warned;

    public Task<IReadOnlyCollection<HeldPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            logger.LogWarning(
                "PlaceholderPositionStore を使用中: 保有ポジションの実データ（RiskManagement:BaseUrl）が未設定のため損切り検知は行われません。");
        }

        return Task.FromResult<IReadOnlyCollection<HeldPosition>>([]);
    }
}
