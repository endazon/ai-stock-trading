namespace RiskManagementService.Application.Ports;

// FR-21, FR-10, FR-06, UC-06, ADR-0016 決定15, #463, IADR-0181:
// **ブローカ建玉の観測が到達した事実を、期間で判定できる形で記録する。**
//
// **［2026-08-08 改定］粒度は「観測が届いた取引日の集合」である**（planning `d9c2014`・裁定 planning#292）。
// 従前の設計（単一の「最終観測時刻」）は**報告期間を観測が覆っていたかを判定できない**ため計画が改めた ——
// 初回観測が 8/20 なら 7 月分の月報は最終観測時刻が非 null であるため「正当な 0」と報告され、
// **FR-21 が塞ごうとした誤読（「強制買戻しは起きていない」と読める）がそのまま残る**。
// 観測が途中で止まった期間（ブローカ接続の障害等）も同じく「正当な 0」として報告され続ける。
//
// **なぜ推定台帳と別に要るのか。** 推定台帳（<see cref="IBuyInInferenceStore"/>）は
// **推定が起きたときにしか行を書かない**。したがって行数 0 は 2 つの別事実を区別できない。
//
//   1. 観測が一度も届いていない（＝**この統制はまったく働いていない**・異常）
//   2. 観測した結果、強制買戻しは 1 件も無かった（＝正常）
//
// **観測の到達を別に記録して初めて、件数を正当な 0 として供給できる**（FR-21・Must）。
public interface IPositionObservationArrivalStore
{
    /// <summary>
    /// 観測の到達を記録する（**取引日ごとに 1 行**・冪等）。
    /// <para>
    /// **推定の有無に関わらず記録する**——これが推定台帳との唯一かつ本質的な違いである。
    /// </para>
    /// </summary>
    /// <param name="tradingDay">観測が属する取引日（基準タイムゾーン。<c>TradingDay.Of</c> で導出する）。</param>
    /// <param name="observedAt">観測時刻（同一取引日の複数観測では最新を保つ）。</param>
    void Record(DateOnly tradingDay, DateTimeOffset observedAt);

    /// <summary>
    /// 期間 [fromInclusive, toInclusive] のうち**観測が届いた取引日**の集合。
    /// <para>
    /// 覆っているかの判定（どの日を「取引日」とみなすか）は呼び出し側が営業日暦とともに行う ——
    /// **ストアは観測された事実だけを返し、判定規則を持たない。**
    /// </para>
    /// </summary>
    IReadOnlyList<DateOnly> GetObservedDaysBetween(DateOnly fromInclusive, DateOnly toInclusive);
}
