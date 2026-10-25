namespace RiskManagementService.Infrastructure.Persistence;

// FR-10, #869, ADR-0041 決定2, IADR-0354 決定4: 基準資金（equity）の**鮮度**の構成（セクション "Risk:CapitalBaseline"）。
//
// 計画（06_technical/05_trading-assumptions §5 注記の 2026-09-19 追加）は「**鮮度は日次でよい**
// （前営業日終値時点の値であり、建玉観測の 60 分とは別の量である）」とだけ定め、**検査の形は定めていない**
// （ADR-0041 §結果 フォローアップ 3）。本オプションは実装判断であり、根拠は IADR-0354 決定4 にある。
//
// **値そのもの（基準資金）を構成から注入する口は作らない。** 供給元はブローカーの口座照会だけである
// （SimulatorProfileOptions が上限値を構成から与えない理由と同じ規律・IADR-0108 決定2/5）。
public sealed class CapitalBaselineOptions
{
    public const string SectionName = "Risk:CapitalBaseline";

    /// <summary>
    /// 基準資金として採る観測の最大経過時間（日）。既定 4 日。
    /// <para>
    /// 🔴 #1423 M-3, ADR-0073 決定1, IADR-0581 決定2: <b>新規建ての可否は「直前の米国東部暦日に成功した観測があるか」で
    /// 決まり、本値はそれに包まれる</b>（直前の暦日の行があれば経過は 2 日を超えない）。本値で新たに止まる場面は無く、
    /// 帰結の区別（長い停止を <c>UnavailableStale</c> として分けて数える）のためだけに残す。
    /// </para>
    /// <para>
    /// 旧根拠: 3 連休を挟むと金曜の観測を火曜に使うことになり、経過は最大およそ <b>3.40 日</b>である
    /// （金 23:55 ET の観測を火曜の寄り付き 09:30 ET に使う場合の実測値）。4 日はこれを通し、
    /// **巡回が 1 営業週にわたり死んでいる状態は通さない**。超えたら「照会できていない」＝新規建てを止める。
    /// </para>
    /// <para>0 以下は既定へ倒す（構成の書き損じで統制が常時 fail-closed になり運用が止まるのを避ける）。</para>
    /// </summary>
    public int MaxAgeDays { get; set; } = DefaultMaxAgeDays;

    public const int DefaultMaxAgeDays = 4;

    public TimeSpan MaxAge => TimeSpan.FromDays(MaxAgeDays > 0 ? MaxAgeDays : DefaultMaxAgeDays);

    /// <summary>
    /// FR-10, FR-09, #1423 M-3, ADR-0073 決定3, IADR-0581 決定3: 観測の欠けを検知して知らせる巡回を動かすか。既定 true。
    /// <para>計画が通知を求める統制であるため既定で有効にする（無効にできるのは試験・障害時の切り分けのため）。</para>
    /// </summary>
    public bool NotifyObservationMissing { get; set; } = true;

    /// <summary>上の巡回の間隔（秒）。既定 300（口座照会の巡回と同じ 5 分）。1 未満は 1 へ丸める。</summary>
    public int ObservationMissingCheckIntervalSeconds { get; set; } = 300;
}
