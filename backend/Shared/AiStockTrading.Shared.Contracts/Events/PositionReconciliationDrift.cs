using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-05, FR-10, FR-11, FR-09, #292, IADR-0118: 取引台帳とブローカ実ポジションの乖離を検知した。
//
// 検知・記録・通知のみで**是正は伴わない**（自動で建玉を合わせにいく発注経路は作らない）。
// 一過性の未反映で鳴らないよう、同一の乖離が連続して観測されたときにだけ発行される。
public record PositionReconciliationDrift(
    IReadOnlyList<PositionDriftItem> Drifts,
    DateTimeOffset ObservedAt,
    DateTimeOffset DetectedAt);
