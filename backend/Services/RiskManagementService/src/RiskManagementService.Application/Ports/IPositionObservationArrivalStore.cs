namespace AiStockTrading.RiskManagement.Application.Ports;

// FR-21, FR-10, FR-06, UC-06, ADR-0016 決定15, #463, IADR-0181:
// **ブローカ建玉の観測が到達した事実**（最終観測時刻）の永続化。
//
// **なぜ台帳と別に要るのか。** 強制買戻しの推定台帳（<see cref="IBuyInInferenceStore"/>）は
// **推定が起きたときにしか行を書かない**。したがって行数 0 は 2 つの別事実を区別できない。
//
//   1. ブローカ建玉の観測が一度も届いていない（＝**この統制はまったく働いていない**・異常）
//   2. 観測した結果、強制買戻しは 1 件も無かった（＝正常）
//
// 前者を 0 件と報告すれば「強制買戻しは起きていない」と読める。計画は名指しでこれを禁じている
// （05_screens SC-03 の供給元の表・ADR-0016 決定15）。**観測の到達を別に記録して初めて、
// 件数を正当な 0 として供給できる** —— これが FR-21（Must・2026-08-07 新設）である。
//
// **未記録（null）は未供給である。** 既定を「観測済み」に倒すと fail-open になる。
public interface IPositionObservationArrivalStore
{
    /// <summary>
    /// 記録されている**最終観測時刻**。一度も観測が届いていなければ <c>null</c>（＝未供給）。
    /// </summary>
    DateTimeOffset? GetLastObservedAt();

    /// <summary>
    /// 観測の到達を記録する。**単調前進のみ**——記録済みの時刻より古い観測では巻き戻さない。
    /// <para>
    /// 順序保証の無いバスでは後着の古い観測が届き得る。巻き戻すと「供給されていた」状態が
    /// 後から「未供給」へ落ち、報告済みの正当な 0 が未供給へ化ける。
    /// </para>
    /// </summary>
    void Record(DateTimeOffset observedAt);
}
