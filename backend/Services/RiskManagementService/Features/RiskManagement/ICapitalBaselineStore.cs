using AiStockTrading.Shared.Contracts.Observability;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, #869, ADR-0041 決定2, IADR-0354: **統制上限の基準資金（equity）の保持。**
//
// 計画（02_requirements FR-10 本文の括弧書き・06_technical/05_trading-assumptions §5 注記）は
// 「**判定に用いる equity は前営業日終値時点の USD 評価額**」と定め、2026-09-19 の裁定（ADR-0041 決定2）で
// **供給元をブローカーの口座照会と確定した**。取引台帳（初期資金 ＋ 実現損益）から導くのをやめる。
//
// 本ストアは口座照会の観測列（<c>BrokerAccountObserved.Account.EquityInBase</c>）を**取引日ごとに 1 行へ畳み**、
// 判定には「**当日より前の取引日で最新の行**」を返す。日中の観測をそのまま返さないのは、計画 §5 注記が
// 「日中の評価損益で上限を動かすと、含み益で上限が緩み含み損で締まるという逆方向の作用が起きる」として
// 明示的に禁じているためである。
//
// **永続でなければならない。** 非永続にすると再起動のたびに「前日の行」が消え、次の取引日境界まで
// 新規建てが丸一日止まる。口座種別の観測（非永続・30 分失効。IADR-0153 決定3）と設計が違うのは、
// あちらが「いまの値」でこちらが「昨日の値」だからである。
public interface ICapitalBaselineStore
{
    /// <summary>
    /// 口座照会で得た評価額を記録する（観測時刻が属する取引日の行を最新で上書きする）。
    /// <b>逆行する観測（その取引日に記録済みの時刻より古いもの）は無視する。</b>
    /// </summary>
    void Record(decimal equityInBase, DateTimeOffset observedAt);

    /// <summary>
    /// 判定に用いる基準資金を返す。
    /// <para>
    /// <b>当日より前の取引日の行が無い、鮮度が切れている、または直前の米国東部暦日に成功した観測が無い
    /// （#1423 M-3・ADR-0073 決定1）場合は <c>null</c> を返す。</b>
    /// 呼び出し側（判定コア）は <c>null</c> を「口座を照会できていない」として**新規建てを止める**
    /// （fail-closed。ADR-0016 決定3・ADR-0028 と同じ形）。**手仕舞い・損切りは止めない。**
    /// </para>
    /// </summary>
    CapitalBaseline? GetCurrent();

    /// <summary>
    /// FR-10, SC-03, #1423 M-3, ADR-0073 決定1・3, IADR-0581: <see cref="GetCurrent"/> と<b>同じ判定</b>を、帰結つきで返す。
    /// <para>
    /// <b>計器を動かさず、ログも出さない。</b> 表示（SC-03）と通知の巡回が「なぜ未供給か」を読むためにある。
    /// 審査（新規建ての可否）は従来どおり <see cref="GetCurrent"/> を使う（読み出しの帰結を数えるのは審査の側だけ）。
    /// </para>
    /// </summary>
    CapitalBaselineReading Read();
}

/// <summary>
/// FR-10, SC-03, #1423 M-3, ADR-0073, IADR-0581: 基準資金を読んだ結果（値と、その帰結）。
/// </summary>
/// <param name="Baseline">判定に用いる基準資金。未供給なら <c>null</c>（新規建ては止まる）。</param>
/// <param name="Outcome">読み出しの帰結。</param>
/// <param name="Today">判定した時点の取引日（米国東部時間の暦日）。</param>
/// <param name="LatestTradingDay">当日より前で最新の行の取引日。行が 1 つも無ければ <c>null</c>。</param>
/// <param name="LatestEquityInBase">その行の評価額。行が 1 つも無ければ <c>null</c>。</param>
public sealed record CapitalBaselineReading(
    CapitalBaseline? Baseline,
    CapitalBaselineReadOutcome Outcome,
    DateOnly Today,
    DateOnly? LatestTradingDay,
    decimal? LatestEquityInBase)
{
    /// <summary>直前の暦日から最新の行までの日数（行が無ければ <c>null</c>。平常は 1）。</summary>
    public int? GapDays => LatestTradingDay is { } d ? Today.DayNumber - d.DayNumber : null;

    /// <summary>
    /// 🔴 ADR-0073 決定1・3: <b>観測が欠けたために</b>新規建てを止めているか（直前の暦日の欠け・鮮度切れ）。
    /// 行が 1 つも無い（初回の配備）・人手で 0 を入れた（意図した停止）は含めない —— 欠けの通知はこの 2 つを鳴らさない。
    /// </summary>
    public bool ObservationMissing =>
        Outcome is CapitalBaselineReadOutcome.UnavailableMissingPreviousDay or CapitalBaselineReadOutcome.UnavailableStale;
}

/// <summary>
/// FR-10, #869, ADR-0041 決定2, IADR-0354: 判定に用いる基準資金と、その**由来**。
/// <para>
/// 値だけでなく取引日・観測時刻を持つのは、<b>「いつ時点の評価額を統制の分母に使ったか」が監査（FR-11）で
/// 読めなければ、事後に統制値の妥当性を検証できない</b>ためである。
/// </para>
/// </summary>
/// <param name="EquityInBase">基準資金（USD）。ブローカーの口座照会に由来する（含み損益を含む）。</param>
/// <param name="TradingDay">その観測が属する取引日（米国東部時間の暦日）。当日より前である。</param>
/// <param name="ObservedAt">照会した時刻（UTC）。鮮度の判定に用いる。</param>
public sealed record CapitalBaseline(decimal EquityInBase, DateOnly TradingDay, DateTimeOffset ObservedAt);
