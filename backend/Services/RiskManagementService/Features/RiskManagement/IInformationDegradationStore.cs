namespace RiskManagementService.Features.RiskManagement;

// FR-01, FR-02, FR-10, ADR-0020 決定2/決定3, #337, #564, IADR-0249, IADR-0267: 情報収集の縮退状態（新規建て停止）の保持。
//
// 収集サービスは欠測の**遷移**でのみ `InformationSourceDegraded` / `InformationSourceRecovered` を発行する
// （#336・洪水抑止）。リスク管理はカテゴリ単位でその状態を畳み、**BlocksNewEntries=true のカテゴリが
// 1 つでも残っていれば新規建てを止める**（判定は RiskEvaluator・拒否理由 InformationSourceDegraded）。
//
// 🔴 **遷移だけでは足りない。** 縮退が続く静かな区間に本サービスが再起動すると、次の遷移まで停止が届かず
// **情報が欠測したまま新規建てが再開しうる**（#564 の fail-open）。よって収集サービスは
// **毎巡回 1 件の現況観測**（`InformationSourceStateObserved`）も発行し、本ポートはそれを
// **有効期間つき**で受ける。**新規建てを通してよいのは「有効な観測が『止めるものは無い』と言っている」ときだけ**であり、
// **観測が無い（起動直後・再起動直後）／失効した状態は「不明」＝止める**へ倒す。
//
// 🔴 **手仕舞い・損切りを止める表現を持たない。** 本ポートが供給するのは「新規建てを止めるか」の
// 1 ビットだけであり、決済側の停止は型として表現できない（CollectionDegradation と同じ構造防御）。
public interface IInformationDegradationStore
{
    /// <summary>新規建てを停止すべき縮退カテゴリを登録する（BlocksNewEntries=true の Degraded 遷移）。</summary>
    void MarkDegraded(string category);

    /// <summary>カテゴリの回復（Recovered 遷移）。未登録のカテゴリは無視する（冪等）。</summary>
    void MarkRecovered(string category);

    /// <summary>
    /// 収集サービスの現況観測を適用する（#564）。<b>停止カテゴリの集合を全量で置き換える。</b>
    /// <para>
    /// 🔴 <b>鮮度を与えるのは本メソッドだけである。</b> 遷移（<see cref="MarkDegraded"/> /
    /// <see cref="MarkRecovered"/>）は集合を出し入れするが鮮度を更新しない ——
    /// <b>1 件の回復は「他のカテゴリも健全である」ことを保証しない</b>ためである。
    /// </para>
    /// <para>
    /// <b>逆行する観測（より古い時刻）は無視する</b>。再配送・順序の入れ替わりで、
    /// 古い現況が新しい遷移を消さないようにする。
    /// </para>
    /// </summary>
    /// <param name="blockingCategories">新規建てを止めるべきカテゴリの全量（空もあり得る＝止めるものが無い）。</param>
    /// <param name="validFor">この観測が有効な期間（発行側の宣言。実装側で上下限にクランプする）。</param>
    /// <param name="observedAt">観測時刻。</param>
    void ApplyObservation(IReadOnlyCollection<string> blockingCategories, TimeSpan validFor, DateTimeOffset observedAt);

    /// <summary>
    /// 新規建てを停止すべきか。
    /// <para>
    /// <b>停止すべき縮退が残っている</b>か、<b>有効な現況観測が無い</b>（未観測・失効）なら <c>true</c>。
    /// <b>「不明なら通す」ではなく「不明なら止める」</b>である（#564）。
    /// </para>
    /// </summary>
    bool BlocksNewEntries { get; }
}
