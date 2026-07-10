using AiStockTrading.InformationCollection.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.InformationCollection.Worker.Composable.Adapters;

// NFR（費用）, IADR-0031: ICostControlGate の暫定実装。費用統制（#23）の実データ（CostControl:BaseUrl）が未設定の間は
// Normal（停止せず・1×）を返す＝間隔延長/停止は行わない。差し替え漏れ検知のため初回利用時に 1 回警告する。
internal sealed class PlaceholderCostControlGate(ILogger<PlaceholderCostControlGate> logger) : ICostControlGate
{
    private int _warned;

    public Task<CostControlGate> GetAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            logger.LogWarning(
                "PlaceholderCostControlGate を使用中: 費用統制の実データ（CostControl:BaseUrl）が未設定のため間隔延長/停止は行われません。");
        }

        return Task.FromResult(CostControlGate.Normal);
    }
}
