extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;

namespace TradeDecisionService.Features.TradeDecision;

// FR-10, #1176, IADR-0495 決定1・2: 新規建ての最小の名目額のしきい値（equity 比）。既定は TradingDefaults.MinEntryNotionalRatio（1%）。
// 🔴 **既定は有効**（不在を「統制なし」にしない。IADR-0163 決定2 の規律）。0 は統制を外す明示の値。範囲外は構築で例外（起動を止める）。
public sealed record MinimumEntryNotionalOptions
{
    public MinimumEntryNotionalOptions(decimal ratio) => Ratio = MinimumEntryNotional.Validate(ratio);

    /// <summary>equity に対するしきい値（0 以上 0.25 以下）。</summary>
    public decimal Ratio { get; }

    public static MinimumEntryNotionalOptions Default { get; } = new(TradingDefaults.MinEntryNotionalRatio);
}
