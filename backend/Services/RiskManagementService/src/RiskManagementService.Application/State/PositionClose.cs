using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Application.State;

// FR-10, FR-11, UC-06, #292, IADR-0117: 利用者（owner）による建玉の手仕舞い要求と、その判定結果。

/// <summary>
/// 決済要求。売買方向は含めない（建玉方向からサーバが決める＝誤方向指定で建て増しさせない）。
/// <see cref="Quantity"/> 省略＝処理中を除いた残り全量。<see cref="LimitPrice"/> 省略＝現在値。
/// </summary>
public sealed record PositionCloseCommand(
    string Symbol,
    Market Market,
    int? Quantity,
    decimal? LimitPrice,
    string Reason);

/// <summary>決済要求を受理できない理由。<see cref="PositionCloseRejection.None"/> が受理。</summary>
public enum PositionCloseRejection
{
    None = 0,

    /// <summary>台帳に当該銘柄の建玉が無い（全決済済みを含む）。</summary>
    PositionNotFound,

    /// <summary>指定数量が 0 以下。</summary>
    InvalidQuantity,

    /// <summary>指定数量が「建玉 − 処理中の決済」を超える（多重投入による在庫超過を含む）。</summary>
    ExceedsAvailable,

    /// <summary>使える価格が無い（指値の指定が非正、かつ現在値も取得できない）。</summary>
    PriceUnavailable,
}

/// <summary>
/// 決済要求の判定結果。受理時は発行すべき 2 イベントを持つ（発行は呼び出し側＝エンドポイント）。
/// 同一 <c>DecisionId</c> で相関する。
/// </summary>
public sealed record PositionCloseOutcome(
    PositionCloseRejection Rejection,
    OrderApproved? Approval = null,
    PositionCloseRequested? Requested = null)
{
    public bool Accepted => Rejection == PositionCloseRejection.None;

    public static PositionCloseOutcome Reject(PositionCloseRejection rejection) => new(rejection);
}
