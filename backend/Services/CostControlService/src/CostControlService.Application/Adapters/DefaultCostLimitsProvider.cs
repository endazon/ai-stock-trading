using AiStockTrading.CostControl.Application.Ports;
using AiStockTrading.Shared.Kernel.Trading;

namespace AiStockTrading.CostControl.Application.Adapters;

// NFR（費用）, IADR-0027/0065 決定 5: 外部依存を持たない既定の上限供給（前提条件の既定値・05_trading-assumptions §6）。
//
// 本番ホスト（Program.cs）が登録するのは AssumptionsCostLimitsProvider の方であり、`Configuration:BaseUrl` 未設定時に
// 既定値へ倒す判断は共有クライアントへ一本化してある（同じ条件を 2 箇所で判定しないため）。本クラスは Application 層に
// 外部依存なしの既定実装を置いておくためのもので、位置づけは同層の InMemoryCostLedger と同じ。
public sealed class DefaultCostLimitsProvider : ICostLimitsProvider
{
    private static readonly MonthlyCostLimits Defaults = TradingAssumptionsDefaults.Create().CostLimits;

    public ValueTask<MonthlyCostLimits> GetLimitsAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Defaults);
}
