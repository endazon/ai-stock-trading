using RiskManagementService.Domain;

namespace RiskManagementService.Application.Ports;

// FR-20, FR-11, #387, 06_daytrading-review §4.1 条件1, IADR-0148: 段階ゲートの「統制違反 0 件」を数えるための
// 発注審査の観測ログ。**未記録＝未供給（null）**であり、「違反 0 件」とは別物である
// （0 を返すと、供給元の不在が合格として読まれる。#387 の fail-open そのもの）。
public interface IControlViolationObservationStore
{
    /// <summary>
    /// 発注審査 1 回ぶんの観測を記録する。<b><c>DecisionId</c> で冪等</b>（再送で二重計上しない）。
    /// </summary>
    void Record(OrderScreeningObservation observation);

    /// <summary>
    /// 現在の観測窓の集計を返す。<b>算入対象（moomoo <c>SIMULATE</c>）の観測が 1 件も無ければ
    /// <c>null</c>（未供給）</b>を返す。
    /// </summary>
    ControlViolationTally? GetTally();

    /// <summary>
    /// 観測窓を区切る（現在窓の観測を破棄する）。
    /// <para>
    /// 計画は「集計期間は <b>Stage 1 の全期間</b>」と定める（§4.1 条件1）。段階遷移が受理された時点で
    /// 窓を区切ると、Stage 1 に居る間の窓は Stage 1 へ入った時刻から始まる＝計画の期間と一致する。
    /// 区切った直後は未供給（＝昇格しない）に戻る（fail-safe）。
    /// </para>
    /// </summary>
    void ResetWindow();
}
