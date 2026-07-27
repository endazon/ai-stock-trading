using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Domain;

namespace AiStockTrading.RiskManagement.Application.Adapters;

// FR-10, FR-12, FR-20, #257, IADR-0108 決定3: SIMULATE 限定のリスク上限プロファイルを**読み取り時**に適用する
// デコレータ。内側のストア（永続化の権威・IADR-0012）はそのままで、返す設定のうち
//   - 金額系の上限 2 項目（MaxOrderAmount / MaxDailyOrderAmount）
//   - ペーパー段階（TradeMode.Paper）の資金上限
// だけを差し替える。比率系・保有銘柄数・取引ガードは内側の設定をそのまま通す（利用者が SC-02 で行った
// 変更を握りつぶさない）。実弾段階（TradeMode.Live）の資金上限は差し替えない（IADR-0108 決定4）。
//
// シード時ではなく読み取り時に上書きするのは、本番既定が既にシードされた検証用 DB でもリセット無しで
// 上限が効くようにするため。DB の設定行は書き換えないため、プロファイルを外せば即座に本番既定へ戻る。
public sealed class SimulatorProfileRiskSettingsStore(IRiskSettingsStore inner) : IRiskSettingsStore
{
    public RiskManagementSettings GetCurrent()
    {
        var current = inner.GetCurrent();
        var profile = SimulatorTradingDefaults.CreateRiskLimits();

        return current with
        {
            Limits = current.Limits with
            {
                MaxOrderAmount = profile.MaxOrderAmount,
                MaxDailyOrderAmount = profile.MaxDailyOrderAmount,
            },
            Stage = SimulatorTradingDefaults.ApplyToPaperStage(current.Stage),
        };
    }

    // 永続化は素通しする（プロファイルはメモリ上の上書きに留め、DB を汚さない）。
    public void Save(RiskManagementSettings settings) => inner.Save(settings);
}
