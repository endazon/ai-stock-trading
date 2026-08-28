using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Domain;

// FR-10, UC-06, #330, IADR-0133 決定7: 自動縮小 1 回分の計画（＝記録先 4 つが写像する唯一の情報源）。
// 04_report-templates の日報 §4 の表が要求する 7 列（時刻を除く 6 列＋明細）をそのまま保持する。
// 時刻は発行側（Application 層の IClock）が付ける（ドメインは時計を持たない）。
public sealed record MaintenanceMarginReductionPlan
{
    /// <summary>決済前の維持率（日報「決済前の維持率」）。</summary>
    public required decimal RatioBefore { get; init; }

    /// <summary>適用した閾値（日報「閾値」）＝ 保有建玉のうち最も厳しいもの（IADR-0133 決定2）。</summary>
    public required decimal Threshold { get; init; }

    /// <summary>回復目標（日報「回復目標（閾値+5pt）」）＝ 閾値 + 5 ポイント（計画 §5・閾値に連動する）。</summary>
    public required decimal RecoveryTarget { get; init; }

    /// <summary>
    /// 決済後の維持率（日報「決済後の維持率」）。全建玉を決済した場合は **null**
    /// （建玉が無くなれば維持率の概念自体が消える）。
    /// </summary>
    public required decimal? RatioAfter { get; init; }

    /// <summary>決済する建玉（**必要証拠金の降順**。UC-06）。空にはならない（空なら計画自体を作らない）。</summary>
    public required IReadOnlyList<MaintenanceMarginReductionLeg> Legs { get; init; }
}

// 決済する建玉 1 件（部分決済を含む）。日報の「決済した建玉（銘柄・方向・数量・必要証拠金）」に対応する。
public sealed record MaintenanceMarginReductionLeg
{
    public required string Symbol { get; init; }

    public required Market Market { get; init; }

    /// <summary>**建玉の**方向（決済注文の売買方向ではない）。日報の「方向（ロング / ショート）」列。</summary>
    public required TradeSide PositionSide { get; init; }

    public required ProductType ProductType { get; init; }

    /// <summary>決済する数量（株数）。建玉数量と等しければ全量決済、少なければ部分決済。</summary>
    public required int Quantity { get; init; }

    /// <summary>決済時の現在値（USD）。決済注文の指値に用いる。</summary>
    public required decimal PriceUsd { get; init; }

    /// <summary>
    /// 決済する数量に対応する必要証拠金（USD）。部分決済のときは建玉の必要証拠金を数量で按分する
    /// （日報が「決済した建玉の必要証拠金」を求めるため。按分以外に部分決済の必要証拠金を表す方法が無い）。
    /// </summary>
    public required decimal RequiredMarginUsd { get; init; }
}
