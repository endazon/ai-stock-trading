using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-05, FR-10, FR-11, #849, IADR-0350 決定 1: ブローカ建玉の**最新の観測**（1 件）の保持。
//
// 乖離の検知（PositionDriftTracker）は観測を突き合わせたら捨てる——残るのはシグネチャだけで、観測時刻が無い。
// 利用者が乖離を台帳へ取り込む操作は「いま観測されているブローカーの建玉へ合わせる」であり、
// **何を・いつ観測したか**を取り込みの瞬間に読めなければならない。
//
// 🔴 **鮮度の判定はここに置かない**（取り込みサービスが時計とともに行う）。ストアは観測された事実だけを返す
// ——IPositionObservationArrivalStore と同じ分担である。
//
// 🔴 **照会不能は「行が更新されない」ことで表れる。** 発注執行は照会できなかったとき観測を発行しない
// （IADR-0118 の fail-safe）。したがって空の Positions は「建玉が無いと観測した」事実であり、不明ではない。
public sealed record BrokerPositionObservation(
    IReadOnlyList<BrokerPositionSnapshot> Positions,
    DateTimeOffset ObservedAt);

public interface IBrokerPositionObservationStore
{
    /// <summary>観測を記録する（最新で上書きする）。<b>逆行する観測（より古い時刻）は無視する。</b></summary>
    void Record(IReadOnlyList<BrokerPositionSnapshot> positions, DateTimeOffset observedAt);

    /// <summary>最新の観測。<b>一度も観測が届いていなければ <c>null</c></b>。</summary>
    BrokerPositionObservation? GetLatest();
}
