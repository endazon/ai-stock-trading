using OrderExecutionService.Features.OrderExecution;

namespace OrderExecutionService.Infrastructure.Persistence;

// #131, FR-05, IADR-0057: 発注前 DecisionId 予約の行モデル。ブローカ発注の「前」にコミットし、
// 「発注成功→永続化失敗」の窓での二重発注を防ぐ。DecisionId を主キーとする（＝一意制約が競合の権威）。
public sealed class OrderDispatchReservationRow
{
    public Guid DecisionId { get; set; }

    public OrderDispatchState State { get; set; }

    public DateTimeOffset ReservedAt { get; set; }

    /// <summary>確定時刻（Reserved の間は null）。</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>ブローカ注文 ID（確定時に記録する。Reserved の間は null）。</summary>
    public string? BrokerOrderId { get; set; }
}
