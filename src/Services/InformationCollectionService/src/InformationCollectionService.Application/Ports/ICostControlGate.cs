namespace AiStockTrading.InformationCollection.Application.Ports;

// NFR（費用）, FR-02, IADR-0031: 定時サイクル（情報収集 poller）が費用統制（#23）へ照会する統制ゲート。
// 実データは費用統制サービスの GET /costs/state を同期照会して供給する（HttpCostControlGate）。
// 依存先障害時は Normal（停止せず・1×）の安全既定に倒す。
public interface ICostControlGate
{
    Task<CostControlGate> GetAsync(CancellationToken cancellationToken = default);
}

// 費用統制の適用値。Halted なら定時サイクルを停止（収集/発行しない）。IntervalMultiplier は間隔倍率（Normal=1・Throttled=2）。
// Halted 時のサーバ側 IntervalMultiplier は 0（無効値）だが、poller は Halted を先に見るため参照しない（IADR-0027/0031）。
public sealed record CostControlGate(bool Halted, decimal IntervalMultiplier)
{
    // 停止も間隔延長もしない通常状態（安全既定）。
    public static CostControlGate Normal { get; } = new(false, 1m);
}
