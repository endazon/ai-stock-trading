namespace AiStockTrading.Shared.Contracts.Events;

// FR-01, FR-02, FR-10, FR-11, #564, ADR-0020 決定2・決定3, IADR-0267: 情報収集の**現況**（新規建てを止めるべき
// 縮退カテゴリの全量）を、収集の 1 巡回ごとに 1 件だけ宣言する観測。
//
// 🔴 **遷移イベント（InformationSourceDegraded / Recovered）では足りないから在る。** 遷移は状態が変わった
// ときにしか出ないため、**縮退が続いている静かな区間に受け手が再起動すると、次の遷移まで停止が届かない**
// （#564。IADR-0249 が fail-open 側の残余リスクとして記録していた事故）。本イベントは
// **「いま何が止まっているか」を毎巡回言い直す**ことで、受け手が**いつ再起動しても 1 巡回で復元できる**ようにする。
// 「静かな期間に状態が引けない」を定期発行で解いた #513（FxRateSourceUsed・IADR-0225）と同型であり、
// **抑止の鍵だけが違う**——あちらは暦日で抑止したが、こちらは**鮮度そのものが統制の入力**であるため巡回ごとに出す。
//
// 🔴 **載せるのは「新規建てを止める」縮退だけである。** BlocksNewEntries=false の縮退（記録・通知のみ／
// 空売り限定）は載せない —— 受け手が Behavior 文字列を再解釈して停止範囲を広げない、という規律
// （IADR-0249 決定1）をそのまま引き継ぐ。**空の集合は「止めるものが無い」という積極的な宣言**である。

/// <param name="BlockingCategories">
/// いま新規建てを止めるべき縮退カテゴリの<b>全量</b>（差分ではない）。受け手は自らの集合を<b>置き換える</b>。
/// <b>空もあり得る</b>——そのときは「観測した結果、止めるものが無い」を意味する。
/// </param>
/// <param name="ValidFor">
/// この観測が有効な期間（＝次の観測までに見込まれる最大の間隔に余裕を見た値）。
/// <para>
/// 🔴 <b>受け手は収集の巡回間隔を知らない</b>ため、発行側が宣言する（<c>BrokerAvailabilityObserved.CoveredInterval</c>
/// と同じ作法・IADR-0150 決定2）。<b>受け手側で上限クランプが掛かる</b>ため、本値を大きくしても
/// 鮮度の要求そのものを消すことはできない。
/// </para>
/// </param>
/// <param name="ObservedAt">観測時刻（UTC）。受け手はこの時刻から <c>ValidFor</c> を数える。</param>
public record InformationSourceStateObserved(
    IReadOnlyList<string> BlockingCategories,
    TimeSpan ValidFor,
    DateTimeOffset ObservedAt)
{
    /// <summary>
    /// 🔴 <b>手仕舞い・損切りは止まらない。</b> ADR-0020 決定2/決定3 は限定縮退でも決済を止めないと定める。
    /// <b>受け手が「縮退＝全停止」と読み違えないよう、イベント自身が明示する</b>（InformationSourceDegraded と同じ）。
    /// </summary>
    public bool ClosesAllowed => true;

    /// <summary>新規建てを止めるべき縮退が 1 つでもあるか（受け手の可読性のための導出値）。</summary>
    public bool BlocksNewEntries => BlockingCategories.Count > 0;
}
