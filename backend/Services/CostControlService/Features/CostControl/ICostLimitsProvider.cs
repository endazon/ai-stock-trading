using AiStockTrading.Shared.Kernel.Trading;

namespace CostControlService.Features.CostControl;

// NFR（費用）, FR-17, IADR-0027/0065: 月次費用上限の供給。実体は #19 のバージョン付き前提条件（設定サービス）から解決する
// （AssumptionsCostLimitsProvider）。設定サービスへ到達できない構成では既定値（DefaultCostLimitsProvider）。
//
// 非同期なのは解決元（IAssumptionsProvider）が非同期のため（IADR-0065 決定 1）。同期のまま待つと、キャッシュミス時の
// HTTP 往復でスレッドプールを占有する（費用計上は全 LLM 呼び出しの後段にあり流量がある経路）。
// 取得不可でも例外を出さず必ず値を返す（fail-safe の縮退は解決側が持つ・IADR-0063 決定 5）。
public interface ICostLimitsProvider
{
    ValueTask<MonthlyCostLimits> GetLimitsAsync(CancellationToken cancellationToken = default);
}
