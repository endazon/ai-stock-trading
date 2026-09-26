using OrderExecutionService.Features.OrderExecution;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Infrastructure.Persistence;

// #131, FR-05, IADR-0057: 発注前 DecisionId 予約の行モデル。ブローカ発注の「前」にコミットし、
// 「発注成功→永続化失敗」の窓での二重発注を防ぐ。DecisionId を主キーとする（＝一意制約が競合の権威）。
public sealed class OrderDispatchReservationRow
{
    public Guid DecisionId { get; set; }

    public OrderDispatchState State { get; set; }

    public DateTimeOffset ReservedAt { get; set; }

    /// <summary>
    /// 確定時刻（Reserved の間は null）。🔴 #876, IADR-0398: Forgone では<b>見送りを記録した時刻</b>
    /// （予約を取る前の見送りでは <see cref="ReservedAt"/> も同じ時刻＝行を作った時刻である）。
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>ブローカ注文 ID（確定時に記録する。Reserved の間は null）。</summary>
    public string? BrokerOrderId { get; set; }

    /// <summary>
    /// 🔴 NFR-09, FR-20, ADR-0045 決定2, #1051, IADR-0444 決定1: 予約を取った時点で送る先のアダプタの発注先（取引環境）。
    /// 解放の門（SIMULATE / 実弾）はこの値で選ぶ。<b>null は不明</b>（本列を足す前の行）であり、どちらの門を開けても解放しない。
    /// 序数は <see cref="BrokerProvider"/> の整数（0＝内蔵 paper / 1＝moomoo REAL / 2＝moomoo SIMULATE）。
    /// </summary>
    public BrokerProvider? BrokerProvider { get; set; }
}
