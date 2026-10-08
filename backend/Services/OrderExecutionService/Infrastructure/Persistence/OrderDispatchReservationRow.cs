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

    /// <summary>
    /// 🔴 FR-10, ADR-0049 決定1, #1122, IADR-0486 決定6: 予約を取った承認の発注意図が運んだ「損切り幅に下限を掛けてラインを引いた」印
    /// （<c>OrderIntent.StopFloorSource</c>）。送信結果が不明のまま突合が発注済みと確定したとき、突合はブローカーの注文から記録を組み直すため
    /// 発注意図の印を持たない——ここに残した値を記録へ写す。<b>null は分からない</b>（列を足す前の行・決済・保護レグ）。
    /// </summary>
    public StopWidthFloorSource? StopFloorSource { get; set; }

    /// <summary>
    /// 🔴 FR-10, UC-06, ADR-0050 決定1, #1253, IADR-0515 追記(1): 予約を取った承認の出どころ（<c>OrderApproved.Origin</c>。<c>Unknown</c> は null で書く）。
    /// 送信結果が不明のまま突合が発注済みと確定したとき、ブローカーの注文から組み直す記録へ写す（S1 の決済の前の取消が利用者の手仕舞い・
    /// 維持率割れの自動縮小を見分けるため）。<b>null は分からない</b>（列を足す前の行・保護の機構の予約・出どころの無い承認）＝取り消す側。
    /// </summary>
    public OrderApprovalOrigin? ApprovalOrigin { get; set; }

    /// <summary>
    /// 🔴 FR-10, UC-06, ADR-0050 決定1, #1262, IADR-0515 追記(2): 予約を取った発注の建て・決済の別（通常の経路が発注の記録に書くのと同じ値）。
    /// 送信結果が不明のまま突合が発注済みと確定したとき、証券会社の照会は建て・決済の別を返さない（moomoo）ため、ここに残した値で記録を書く
    /// （書かないと、利用者の手仕舞い・保護の機構の決済が新規建てとして残り、処理中の決済の読み出しに載らない）。
    /// <b>null は分からない</b>（列を足す前の行）＝照会の値のまま（是正前と同じ）。
    /// </summary>
    public PositionEffect? PositionEffect { get; set; }
}
