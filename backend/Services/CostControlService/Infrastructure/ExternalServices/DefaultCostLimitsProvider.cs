using CostControlService.Features.CostControl;
using AiStockTrading.Shared.Kernel.Trading;

namespace CostControlService.Infrastructure.ExternalServices;

// NFR（費用）, IADR-0027/0065 決定 5: 外部依存を持たない既定の上限供給（前提条件の既定値・05_trading-assumptions §6）。
//
// 本番ホスト（Program.cs）が登録するのは AssumptionsCostLimitsProvider の方であり、`Configuration:BaseUrl` 未設定時に
// 既定値へ倒す判断は共有クライアントへ一本化してある（同じ条件を 2 箇所で判定しないため）。本クラスは外部依存なしの
// 既定実装を置いておくためのもので、ICostLimitsProvider のもう一方の実装（AssumptionsCostLimitsProvider）と同じ
// Infrastructure/ExternalServices/ に置く（同一ポートの実装を 1 箇所へ集める。作業仕様書 20260829_w11s4c 参照）。
// 挙動としての位置づけは Infrastructure/Persistence/ の InMemoryCostLedger と同じ（本番未登録・テスト専用の縮退実装）。
public sealed class DefaultCostLimitsProvider : ICostLimitsProvider
{
    private static readonly MonthlyCostLimits Defaults = TradingAssumptionsDefaults.Create().CostLimits;

    public ValueTask<MonthlyCostLimits> GetLimitsAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Defaults);
}
