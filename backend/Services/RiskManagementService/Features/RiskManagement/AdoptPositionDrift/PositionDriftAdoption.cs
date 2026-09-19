using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement.AdoptPositionDrift;

// FR-10, FR-11, UC-06, ADR-0003, #849, IADR-0350: 利用者（owner）による乖離の取り込み要求と、その判定結果。

/// <summary>
/// 取り込み要求。<b>数量を含めない</b>——目標は「いま観測されているブローカーの建玉」であり、
/// 利用者が任意の数量で台帳を書き換える操作にしない。
/// </summary>
public sealed record PositionDriftAdoptionCommand(string Symbol, Market Market, string Reason);

/// <summary>取り込みを受理できない理由。<see cref="None"/> が受理。<b>いずれの拒否も台帳を書かない。</b></summary>
public enum PositionDriftAdoptionRejection
{
    None = 0,

    /// <summary>理由が空。</summary>
    ReasonRequired,

    /// <summary>ブローカ建玉の観測が一度も届いていない（照会不能を含む＝不明）。</summary>
    ObservationUnavailable,

    /// <summary>最新の観測が古い（観測が止まっている）。古い値へ台帳を合わせない。</summary>
    ObservationStale,

    /// <summary>当該銘柄に乖離が無い（<b>取り込み済みを含む</b>）。</summary>
    NoDrift,

    /// <summary>その乖離がまだ報告されていない（連続観測条件を満たしていない＝一過性の未反映かもしれない）。</summary>
    DriftNotReported,

    /// <summary>台帳に無い建玉・数量の増加・方向の反転。取得単価も損切りも無い建玉を台帳へ作らない。</summary>
    UnsupportedDirection,

    /// <summary>観測より後に台帳の当該銘柄が動いた（観測が台帳に対して古い）。</summary>
    LedgerMovedAfterObservation,

    /// <summary>処理中の決済（承認済み・未約定）がある。約定が後から届くと二重に減る。</summary>
    CloseInFlight,
}

/// <summary>取り込みの判定結果。受理時は発行すべき監査イベントを持つ（発行は呼び出し側＝エンドポイント）。</summary>
public sealed record PositionDriftAdoptionOutcome(
    PositionDriftAdoptionRejection Rejection,
    PositionDriftAdopted? Adopted = null)
{
    public bool Accepted => Rejection == PositionDriftAdoptionRejection.None;

    public static PositionDriftAdoptionOutcome Reject(PositionDriftAdoptionRejection rejection) => new(rejection);
}
