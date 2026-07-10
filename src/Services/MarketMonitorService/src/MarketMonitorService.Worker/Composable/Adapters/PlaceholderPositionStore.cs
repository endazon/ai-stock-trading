using AiStockTrading.MarketMonitor.Application.Ports;
using AiStockTrading.MarketMonitor.Domain;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.MarketMonitor.Worker.Composable.Adapters;

// FR-03, FR-10: IPositionStore の暫定実装。保有・損切り価格の実データは発注執行（#13）・損益/監査（#17）連携で供給される。
// それらが未実装の現段階は「保有なし」を返す（＝損切り検知対象なし）。差し替え漏れ検知のため初回利用時に 1 回警告する
// （#12 の PlaceholderPortfolioStateProvider を踏襲。singleton 登録・GetOpenPositions は毎巡回呼ばれるためログ氾濫を避ける）。
internal sealed class PlaceholderPositionStore(ILogger<PlaceholderPositionStore> logger) : IPositionStore
{
    private int _warned;

    public IReadOnlyCollection<HeldPosition> GetOpenPositions()
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            logger.LogWarning(
                "PlaceholderPositionStore を使用中: 保有・損切り価格の実データ（#13/#17）が入るまで損切り検知は行われません。");
        }

        return [];
    }
}
