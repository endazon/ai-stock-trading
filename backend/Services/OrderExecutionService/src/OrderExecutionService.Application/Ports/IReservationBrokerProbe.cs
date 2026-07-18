using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.OrderExecution.Application.Ports;

// #141, FR-05, IADR-0074: 滞留した Reserved 予約の「ブローカに実際に発注されたか」をブローカへ照会するポート。
//
// Reserved 予約は DecisionId だけを持ちブローカ注文 ID を持たないため、IBrokerAdapter.GetOrderAsync（注文 ID 照会）
// では確定できない。実照会は DecisionId を client order id として伝播するか時刻窓で突き合わせる必要があり、いずれも
// 実 OpenD 依存（受け入れ基準3・#82 系 E2E）。本ポートで抽象化し、既定は no-op（常に Indeterminate）とする。
public interface IReservationBrokerProbe
{
    /// <summary>
    /// DecisionId に対応する注文の実状態をブローカへ照会する。判定不能・照会不達は必ず
    /// <see cref="ReservationProbeOutcome.Indeterminate"/> を返すこと（二重発注を招かない fail-safe）。
    /// </summary>
    Task<ReservationProbeResult> ProbeAsync(Guid decisionId, CancellationToken cancellationToken = default);
}

// #141, IADR-0074: プローブ照会の3値。「不明（Indeterminate）」を必ず表現できることが安全性の要
// （照会不達を安全側＝据え置きに倒すため）。
public enum ReservationProbeOutcome
{
    /// <summary>ブローカに当該注文が**確実に存在**する（発注済み）。<see cref="ReservationProbeResult.Order"/> を伴う。</summary>
    Placed = 0,

    /// <summary>ブローカに当該注文が**確実に存在しない**（未発注）。予約は安全に解放できる。</summary>
    NotPlaced = 1,

    /// <summary>照会不達・判定不能。予約は据え置く（人手/`_error`）。二重発注を招く解放・終端化はしない。</summary>
    Indeterminate = 2,
}

// #141, IADR-0074: プローブ照会の結果。Placed のときのみ Order（ブローカが持つ注文実体）を伴う。
public sealed record ReservationProbeResult(ReservationProbeOutcome Outcome, BrokerOrder? Order)
{
    /// <summary>照会不達・判定不能（据え置き）。</summary>
    public static readonly ReservationProbeResult Indeterminate =
        new(ReservationProbeOutcome.Indeterminate, null);

    /// <summary>確実に未発注（解放可）。</summary>
    public static readonly ReservationProbeResult NotPlaced =
        new(ReservationProbeOutcome.NotPlaced, null);

    /// <summary>確実に発注済み（終端化）。ブローカが持つ注文実体を伴う。</summary>
    public static ReservationProbeResult Placed(BrokerOrder order) =>
        new(ReservationProbeOutcome.Placed, order ?? throw new ArgumentNullException(nameof(order)));
}
