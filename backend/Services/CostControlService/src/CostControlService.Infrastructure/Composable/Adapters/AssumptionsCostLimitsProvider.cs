using ConfigurationService.Client.Ports;
using CostControlService.Application.Ports;
using AiStockTrading.Shared.Kernel.Trading;

namespace CostControlService.Infrastructure.Adapters;

// NFR（費用）, FR-17, #139, IADR-0065 決定 2: 月次費用上限を #19 のバージョン付き全体前提条件から供給する。
// これにより、利用者が設定サービスで上限を変更するとしきい値判定（80%/100%）に反映される。
//
// **本アダプタは意図的に薄い**。キャッシュ・`AssumptionsChanged` による無効化・fail-safe は共有クライアント
// （IADR-0063・CachedAssumptionsProvider）が既に解いており、ここで持ち直すと二重キャッシュになって
// 無効化の届かない層が生まれ、#19 が修正した「版更新の取りこぼし」を再導入する（IADR-0065 決定 4）。
//
// fail-safe の向き（IADR-0063 決定 5 の継承・IADR-0065 決定 3）: 取得不可時の縮退は解決側が
// ① last known good → ② 既定値 の順で行う。**既定値へ即座に戻さない**のは費用統制では特に重要で、
// 利用者が LLM 上限を 15,000 → 5,000 へ絞っていた場合、既定へ戻すと上限が緩む＝浪費側へ倒れるため。
// 未解決（一度も取得できていない）かどうかは VersionedAssumptions.IsResolved で判るが、いずれにせよ
// 「その時点で最も利用者の意図に近い上限」が返るため、本アダプタは版を見ずに CostLimits を取り出すだけでよい。
internal sealed class AssumptionsCostLimitsProvider(IAssumptionsProvider assumptions) : ICostLimitsProvider
{
    public async ValueTask<MonthlyCostLimits> GetLimitsAsync(CancellationToken cancellationToken = default)
    {
        var current = await assumptions.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return current.Assumptions.CostLimits;
    }
}
