namespace AiStockTrading.RiskManagement.Domain;

// FR-20, #333, 06_daytrading-review §4.1〜§4.3, INDEX 決定 34・42, IADR-0137:
// Stage 1（moomoo SIMULATE）の進捗＝「実際に取引できた日数」と「取引件数」の純ロジック。
//
// 計画（§4.2）は期間を**経過日数ではなく実際に取引できた日数**で数えることを定める。
//   - 起算点＝Stage 1 遷移日 ／ 目標 60 営業日
//   - **その日の実際の通常取引時間の 50% 以上**が稼働していれば 1 日（50% 未満は算入しない）
//   - 分母は**その日の実際の通常取引時間**（通常日 6.5 時間／半日取引日 3.5 時間）。固定の 6.5 時間を用いない
//   - 判定の基準時刻は**米国東部時間**（サマータイムの切替・半日取引日に対応する）
//   - 除外＝OpenD の停止・ブローカー側の障害・市場休場

/// <summary>
/// FR-20, #333, 06_daytrading-review §4.2: Stage 1 の 1 営業日ぶんの稼働観測。
/// <para>
/// **その日の通常取引時間の長さを実装は導出しない。** 計画は分母を「その日の実際の通常取引時間
/// （通常日 6.5 時間／半日取引日 3.5 時間）」と定めるだけで、**ある日が半日取引日かをどこから知るかを
/// 述べていない**。カレンダーを実装が発明すると、カレンダーの誤りがそのまま昇格判定の誤りになる
/// （[ADR-0022 決定3] が別件で「営業日カレンダーを保持しない」と裁定した向きとも整合する）。
/// よって<b>観測記録として受け取る</b>（IADR-0137 決定1）。
/// </para>
/// <para>
/// サマータイムの切替も同じ理由で吸収される。<see cref="SessionDateEasternTime"/> は
/// **米国東部時間での取引日**であり、<see cref="RegularSessionMinutes"/> はその日に実際に開いていた
/// 通常取引時間の分数である。実装はタイムゾーン変換を行わない。
/// </para>
/// </summary>
/// <param name="SessionDateEasternTime">米国東部時間での取引日（判定の基準時刻。§4.2）。</param>
/// <param name="RegularSessionMinutes">
/// その日の実際の通常取引時間（分）。通常日 390 分（6.5 時間）／半日取引日 210 分（3.5 時間）。
/// **市場休場日は 0**（プレ／アフターマーケットは含めない。§4.2）。
/// </param>
/// <param name="OperationalMinutes">
/// 上記のうち実際に稼働していた分数。**OpenD の停止・ブローカー側の障害はここが減る形で表れる**
/// （別枠の除外フラグを設けない。§4.2 の「除外」3 事由のうち市場休場は分母 0 で表される）。
/// </param>
public sealed record Stage1TradingDayObservation(
    DateOnly SessionDateEasternTime,
    int RegularSessionMinutes,
    int OperationalMinutes);

/// <summary>
/// FR-20, #333, 06_daytrading-review §4.2: 1 日を「営業日 1 日」として算入するかの判定（純関数）。
/// </summary>
public static class Stage1DayQualification
{
    /// <summary>
    /// 算入に要する稼働率の下限 **0.50**。計画は「その日の実際の通常取引時間の **50% 以上**が稼働している
    /// こと。50% 未満の日は算入しない」と定める——**ちょうど 50% は算入する**（§4.2 / INDEX 決定 34）。
    /// </summary>
    public const decimal MinimumUptimeRatio = 0.50m;

    /// <summary>通常日の通常取引時間（分）＝ 6.5 時間（9:30〜16:00 ET）。観測値の妥当性確認の目安。</summary>
    public const int RegularSessionMinutesFullDay = 390;

    /// <summary>半日取引日の通常取引時間（分）＝ 3.5 時間（9:30〜13:00 ET）。</summary>
    public const int RegularSessionMinutesHalfDay = 210;

    /// <summary>
    /// その日の稼働率。分母は**その日の実際の通常取引時間**であり、固定の 6.5 時間ではない。
    /// 市場休場（分母 0 以下）は稼働率が定義できないため 0 とする。
    /// 稼働分数が分母を超える異常入力は 1.0 に丸める（時間外の稼働で算入を買えないようにする）。
    /// </summary>
    public static decimal UptimeRatio(Stage1TradingDayObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (observation.RegularSessionMinutes <= 0)
        {
            return 0m;
        }

        var operational = Math.Max(0, observation.OperationalMinutes);
        var ratio = (decimal)operational / observation.RegularSessionMinutes;
        return Math.Min(1m, ratio);
    }

    /// <summary>
    /// その日を Stage 1 の営業日 1 日として算入するか。
    /// <para>
    /// **市場休場日（<c>RegularSessionMinutes &lt;= 0</c>）は算入しない**（§4.2 の「除外」）。
    /// OpenD の停止・ブローカー障害は <c>OperationalMinutes</c> の減少として現れ、稼働率が 50% を割れば
    /// 自動的に算入されない。除外事由ごとの専用フラグは設けない——事由が増えるたびに判定が分岐し、
    /// 「どの事由なら除外か」の解釈が実装に入り込むためである。
    /// </para>
    /// </summary>
    public static bool Qualifies(Stage1TradingDayObservation observation) =>
        UptimeRatio(observation) >= MinimumUptimeRatio;

    /// <summary>観測の並びから算入日数を数える（純関数）。</summary>
    public static int CountQualifiedDays(IEnumerable<Stage1TradingDayObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        return observations.Count(Qualifies);
    }
}

/// <summary>
/// FR-20, #333, 06_daytrading-review §4.1〜§4.3, INDEX 決定 34・42: Stage 1 → Stage 2 の合格条件の閾値。
/// <para>
/// **件数の引き下げは行わない**（§4.3）。100 件は「30 件が床・100 件が実用最低限」という実務上の一致点の
/// 下限であり、下げると条件 3 の目的（運用に足るかを統計的に判断できる）そのものが消える。
/// </para>
/// </summary>
/// <param name="TargetTradingDays">目標営業日数 **60**（3 か月相当。§4.2）。</param>
/// <param name="MinimumTradeCount">最小取引件数 **100**（§4.1 条件 3）。</param>
/// <param name="MaximumTradingDays">
/// 打ち切りとなる累計営業日数 **120**（60 ＋ 延長 60。§4.3）。
/// これを経ても件数に届かなければ **Stage 1 を打ち切り Stage 0 へ差し戻す**。
/// </param>
public sealed record Stage1GateCriteria(
    int TargetTradingDays,
    int MinimumTradeCount,
    int MaximumTradingDays)
{
    /// <summary>計画の確定値（60 営業日 / 100 件 / 累計 120 営業日で打ち切り）。</summary>
    public static Stage1GateCriteria Default => new(
        TargetTradingDays: 60,
        MinimumTradeCount: 100,
        MaximumTradingDays: 120);
}

/// <summary>Stage 1 の進捗判定の結果（§4.3 の 3 行 ＋ 期間未達）。</summary>
public enum Stage1GateOutcome
{
    /// <summary>目標営業日数に未達。昇格しない。</summary>
    InProgress,

    /// <summary>
    /// 期間は満たしたが件数が不足。**昇格しない。** Stage 1 を継続し、件数に達した時点で昇格判定を行う
    /// （期間の要件は既に満たしているため再度 60 営業日を要しない。§4.3）。
    /// </summary>
    Extended,

    /// <summary>期間・件数の**両方**を満たした。§4.1 の他の条件を満たせば昇格できる。</summary>
    Promotable,

    /// <summary>
    /// 累計 120 営業日を経ても件数に届かない。**Stage 1 を打ち切り、Stage 0 へ差し戻す**（§4.3）。
    /// 件数不足が「サンプルが足りない」ではなく「戦略が想定した頻度で発火していない」ことの兆候であり、
    /// 延長ではなく設計の見直しが正しい対処であるため（§4.3 の理由）。
    /// </summary>
    Exhausted,
}

/// <summary>
/// FR-20, #333: Stage 1 の進捗（算入された営業日数と取引件数）。
/// <para>
/// **供給元は本 issue の範囲外である。** 既定（0 / 0）は fail-safe であり、供給が無い限り昇格しない。
/// </para>
/// </summary>
/// <param name="QualifiedTradingDays">
/// <see cref="Stage1DayQualification.Qualifies"/> を満たした日の累計（＝実際に取引できた日数）。
/// </param>
/// <param name="TradeCount">Stage 1（SIMULATE）で成立した取引件数。</param>
public sealed record Stage1Progress(int QualifiedTradingDays, int TradeCount);

/// <summary>
/// FR-20, #333, 06_daytrading-review §4.3, INDEX 決定 42: 期間 × 件数の 2 条件と 120 営業日打ち切りの判定（純関数）。
/// </summary>
public static class Stage1Gate
{
    /// <summary>
    /// 進捗から Stage 1 の状態を判定する。
    /// <para>
    /// **打ち切り（<see cref="Stage1GateOutcome.Exhausted"/>）の判定を件数充足より先に行わない。**
    /// 120 営業日に達していても件数を満たしていれば昇格できる（§4.3 の表は「120 営業日を経ても
    /// **100 件に届かない**」場合を打ち切りとしており、期間の超過そのものは打ち切り事由ではない）。
    /// </para>
    /// </summary>
    public static Stage1GateOutcome Evaluate(Stage1Progress progress, Stage1GateCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(criteria);

        var periodMet = progress.QualifiedTradingDays >= criteria.TargetTradingDays;
        var countMet = progress.TradeCount >= criteria.MinimumTradeCount;

        // 期間・件数の**両方**を満たすまで昇格しない（§4.3 の決定）。
        if (periodMet && countMet)
        {
            return Stage1GateOutcome.Promotable;
        }

        if (!periodMet)
        {
            return Stage1GateOutcome.InProgress;
        }

        // ここに来るのは「期間は満たしたが件数が不足」の場合だけである。
        // 累計 120 営業日で打ち切り、Stage 0 へ差し戻す（延長を無制限にしない。§4.3）。
        return progress.QualifiedTradingDays >= criteria.MaximumTradingDays
            ? Stage1GateOutcome.Exhausted
            : Stage1GateOutcome.Extended;
    }
}
