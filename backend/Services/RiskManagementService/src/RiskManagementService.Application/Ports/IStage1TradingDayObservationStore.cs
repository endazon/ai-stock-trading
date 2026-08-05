using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Application.Ports;

// FR-20, FR-12, #385, 06_daytrading-review §4.2, IADR-0150: 段階ゲートの「60 営業日」（§4.2 の期間カウント）を
// 数えるための**稼働の観測ログ**。1 取引日 × 1 発注先 で 1 行。
//
// **0 は fail-safe である。** 観測が 1 件も無ければ 0 日＝期間条件が未充足＝昇格しない。したがって
// 統制違反件数（#387・IADR-0148）のように「未供給」と「0」を型で区別する仕組みは設けない
// （取引件数・IADR-0149 決定3 と同じ判断）。
public interface IStage1TradingDayObservationStore
{
    /// <summary>
    /// 稼働の観測 1 件を積む。<b>(取引日, 発注先) で冪等</b>——同じ観測（同一分・逆行）を二重に積まない
    /// （純関数 <c>Stage1SessionUptime.Credit</c> の規則がそのまま担保する）。
    /// </summary>
    /// <param name="sessionDateEasternTime">米国東部時間での取引日。</param>
    /// <param name="provider">その稼働の発注先（許可制の判定に用いる）。</param>
    /// <param name="observedMinuteOfDayEasternTime">観測時刻（米国東部時間の 0 時からの分数）。</param>
    /// <param name="coveredMinutes">その観測が遡って保証する最大の分数（＝probe の巡回間隔）。</param>
    void CreditUptime(
        DateOnly sessionDateEasternTime,
        BrokerProvider provider,
        int observedMinuteOfDayEasternTime,
        int coveredMinutes);

    /// <summary>
    /// 現在の観測窓で算入された営業日数（§4.2 の「実際に取引できた日数」）。
    /// <b>記録が無ければ 0</b>＝期間条件が未充足＝昇格しない。
    /// </summary>
    int GetQualifiedTradingDayCount();

    /// <summary>
    /// FR-20, SC-03, IADR-0142 決定3: 内蔵 <c>paper</c> 稼働により算入されなかった営業日数（進捗表示の併記用）。
    /// 合格判定には用いない。
    /// </summary>
    int GetExcludedInternalPaperDayCount();

    /// <summary>
    /// 観測窓を区切る（現在窓の観測を破棄する）。
    /// <para>
    /// 計画は Stage 1 の起算点を「<b>Stage 1 遷移日</b>」と定める（§4.2）。段階遷移が受理された時点で窓を区切ると、
    /// Stage 1 に居る間の窓は Stage 1 へ入った時刻から始まる＝計画の期間と一致する。
    /// 区切った直後は 0 日（＝昇格しない）に戻る（fail-safe）。統制違反（#387）・取引件数（#386）と同条件であり、
    /// 3 指標が同じ「Stage 1 の期間」を指す。
    /// </para>
    /// </summary>
    void ResetWindow();
}
