using AiStockTrading.Shared.Contracts.Trading;

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
/// <param name="Provider">
/// FR-20, #334, IADR-0142 決定1: その日に稼働していた<b>発注先</b>。
/// <para>
/// 計画は「Stage 1 の合格判定は <c>SIMULATE</c> の約定のみで集計し、<b>内蔵 <c>paper</c> の約定・稼働日数を
/// 算入してはならない</b>」と定める（FR-20）。<b>既定値を与えない</b>——省略できるようにすると
/// 書き忘れが「算入される側」へ倒れ、外部へ一度も発注していない擬似約定で合格証跡が積み上がる。
/// </para>
/// </param>
public sealed record Stage1TradingDayObservation(
    DateOnly SessionDateEasternTime,
    int RegularSessionMinutes,
    int OperationalMinutes,
    BrokerProvider Provider);

/// <summary>
/// FR-20, #334, #386, IADR-0142 決定1, IADR-0149 決定2: Stage 1 の取引件数を数えるための約定 1 件の観測。
/// <para>
/// 発注先を<b>必須</b>で伴う。件数（100 件）は Stage 1 の合格条件そのものであり
/// （06_daytrading-review §4.1 条件 3）、内蔵 <c>paper</c> の擬似約定が 1 件でも混ざると
/// 合格証跡の正当性が失われる。
/// </para>
/// <para>
/// <b>計上単位は「約定が成立した新規建て注文 1 件」である</b>（IADR-0149 決定2）。計画は 100 件の単位を
/// 定義していないため、<see cref="DecisionId"/>（1 取引判断＝1 注文）と <see cref="PositionEffect"/> を
/// 観測に含め、単位を型の上で表現する。分割約定・イベント再送で同じ注文が複数回観測されても
/// <see cref="Stage1Aggregation.CountTrades"/> が 1 件に畳む。
/// </para>
/// </summary>
/// <param name="DecisionId">
/// 取引判断の ID。<b>計上単位を担保する鍵</b>であり、観測ログの主キーでもある。
/// <c>OrderExecuted.FilledQuantity</c> はブローカの<b>累積</b>約定数であり、同一注文について
/// <c>Accepted</c>(0) → 部分約定 → 全量約定と複数回発行される（IADR-0113）。イベントを数えると
/// 1 注文が 2〜3 件になるため、鍵で畳む。
/// </param>
/// <param name="SessionDateEasternTime">米国東部時間での取引日（日次の突合・監査のため）。</param>
/// <param name="Provider">その約定の発注先。<b>実際に発注したアダプタの値</b>である（IADR-0149 決定1）。</param>
/// <param name="PositionEffect">
/// その注文の建玉効果。<b>新規建て（<see cref="AiStockTrading.Shared.Contracts.Trading.PositionEffect.Open"/>）だけを数える</b>
/// （IADR-0149 決定2）。手仕舞いを別件として数えると、計画が比較に用いた「1 日 3〜5 件」（新規建ての件数）と
/// 単位が食い違い、100 件の意味が変わる。
/// </param>
public sealed record Stage1FillObservation(
    Guid DecisionId,
    DateOnly SessionDateEasternTime,
    BrokerProvider Provider,
    PositionEffect PositionEffect);

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
    /// FR-20, #334, IADR-0142 決定2: Stage 1 の集計に<b>算入してよい発注先</b>。
    /// <para>
    /// <b>許可制である</b>（拒否リストではない）。計画は「<c>SIMULATE</c> の約定のみで集計」と定めるだけで
    /// <c>MoomooReal</c> の扱いを名指ししていない。拒否リスト方式にすると、将来増える発注先が既定で
    /// 合格証跡へ流れ込む。名指しで許可されるまで算入しない。
    /// </para>
    /// </summary>
    public const BrokerProvider CountedProvider = BrokerProvider.MoomooSimulate;

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
    /// FR-20, #333, §4.2: その日が<b>稼働率の条件</b>（実際の通常取引時間の 50% 以上）を満たすか。
    /// <para>
    /// **市場休場日（<c>RegularSessionMinutes &lt;= 0</c>）は満たさない**（§4.2 の「除外」）。
    /// OpenD の停止・ブローカー障害は <c>OperationalMinutes</c> の減少として現れ、稼働率が 50% を割れば
    /// 自動的に外れる。除外事由ごとの専用フラグは設けない——事由が増えるたびに判定が分岐し、
    /// 「どの事由なら除外か」の解釈が実装に入り込むためである。
    /// </para>
    /// <para>
    /// <b>本判定は発注先を見ない。</b>「稼働はしていたが内蔵 <c>paper</c> だった日」を
    /// 除外日数として別掲する（FR-20）ために、稼働の条件と発注先の条件を分けて持つ必要がある。
    /// 算入の可否そのものは <see cref="Qualifies"/> を用いること。
    /// </para>
    /// </summary>
    public static bool MeetsUptimeThreshold(Stage1TradingDayObservation observation) =>
        UptimeRatio(observation) >= MinimumUptimeRatio;

    /// <summary>
    /// その日を Stage 1 の営業日 1 日として算入するか。
    /// <para>
    /// FR-20, #334, IADR-0142: 稼働率の条件（<see cref="MeetsUptimeThreshold"/>）に加えて、
    /// <b>発注先が <see cref="CountedProvider"/>（moomoo <c>SIMULATE</c>）であること</b>を要する。
    /// 内蔵 <c>paper</c> で稼働した日は、稼働率が 100% でも算入しない——外部へ一度も発注していない
    /// 擬似約定の日を数えると、60 営業日という合格証跡がデバッグ稼働で積み上がる。
    /// </para>
    /// </summary>
    public static bool Qualifies(Stage1TradingDayObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return observation.Provider == CountedProvider && MeetsUptimeThreshold(observation);
    }

    /// <summary>観測の並びから算入日数を数える（純関数）。内蔵 <c>paper</c> の日は数えない。</summary>
    public static int CountQualifiedDays(IEnumerable<Stage1TradingDayObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        return observations.Count(Qualifies);
    }

    /// <summary>
    /// FR-20, SC-03, #334, IADR-0142 決定3: <b>内蔵 <c>paper</c> 稼働により除外された営業日数</b>。
    /// <para>
    /// 数えるのは「発注先が内蔵 <c>paper</c> であり、<b>かつ稼働率の条件は満たしていた</b>日」に限る。
    /// 市場休場日・稼働率不足の日はもともと算入されない日であり、<c>paper</c> を理由に除外されたわけではない。
    /// 混ぜると画面の「<c>paper</c> 稼働により N 日を除外」という説明が嘘になる。
    /// </para>
    /// </summary>
    public static int CountExcludedInternalPaperDays(IEnumerable<Stage1TradingDayObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        return observations.Count(o =>
            o.Provider == BrokerProvider.InternalPaper && MeetsUptimeThreshold(o));
    }
}

/// <summary>
/// FR-20, #334, IADR-0142: Stage 1 の進捗を観測から組み立てる（純関数）。
/// <para>
/// <b>集計の供給元は本 issue の範囲外である</b>（日次の稼働分数を記録するドライバ・約定と発注先を結び付ける
/// 経路は <see href="https://github.com/endazon/ai-stock-trading/issues/386">#386</see> が担う）。
/// 本クラスが担保するのは「内蔵 <c>paper</c> が混入し得ない」という構造だけである。
/// </para>
/// </summary>
public static class Stage1Aggregation
{
    /// <summary>その発注先の実績を Stage 1 の合格判定へ算入してよいか（許可制・IADR-0142 決定2）。</summary>
    public static bool IsCounted(BrokerProvider provider) =>
        provider == Stage1DayQualification.CountedProvider;

    /// <summary>
    /// FR-20, #386, IADR-0149 決定2: その約定を Stage 1 の取引件数 1 件として数えるか。
    /// <para>
    /// 算入対象の発注先（<c>SIMULATE</c> の許可制）であり、<b>かつ新規建て</b>であること。
    /// 手仕舞い（<c>Close</c>）を数えないのは、計画が 100 件を比較した出典（個人デイトレーダーは 1 日 3〜5 件）と
    /// 05_trading-assumptions §5 の「1 注文上限いっぱいでも 6 件（＝2 回転）」が、いずれも<b>新規建ての件数</b>を
    /// 数えているためである（1 日あたりの発注金額上限は手仕舞いを算入しない）。条件 3 の目的
    /// （勝率・平均損益を推定できる標本数）にとっても、標本は往復 1 回＝新規建て 1 件に対応する。
    /// </para>
    /// </summary>
    public static bool CountsAsTrade(Stage1FillObservation fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        return IsCounted(fill.Provider) && fill.PositionEffect == PositionEffect.Open;
    }

    /// <summary>
    /// 約定の並びから Stage 1 の取引件数を数える。<c>SIMULATE</c> 以外は 1 件も数えない。
    /// <para>
    /// <b>同一 <c>DecisionId</c> は 1 件に畳む</b>（IADR-0149 決定2）。分割約定はブローカの累積約定数を
    /// 運ぶ複数のイベントとして現れるため（IADR-0113）、畳まないと 1 注文が 2〜3 件に膨らむ。
    /// 膨らむ側は「合格に早く届く」＝緩い側であり、fail-safe に反する。
    /// </para>
    /// </summary>
    public static int CountTrades(IEnumerable<Stage1FillObservation> fills)
    {
        ArgumentNullException.ThrowIfNull(fills);
        return fills.Where(CountsAsTrade).Select(f => f.DecisionId).Distinct().Count();
    }

    /// <summary>
    /// 稼働日と約定の観測から Stage 1 の進捗を組み立てる。
    /// 算入されなかった <c>paper</c> 稼働日は<b>除外日数として別掲する</b>（FR-20・SC-03）。
    /// </summary>
    public static Stage1Progress Aggregate(
        IEnumerable<Stage1TradingDayObservation> days,
        IEnumerable<Stage1FillObservation> fills)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(fills);

        var materializedDays = days as IReadOnlyCollection<Stage1TradingDayObservation> ?? [.. days];
        return new Stage1Progress(
            QualifiedTradingDays: Stage1DayQualification.CountQualifiedDays(materializedDays),
            TradeCount: CountTrades(fills),
            ExcludedInternalPaperDays: Stage1DayQualification.CountExcludedInternalPaperDays(materializedDays));
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
/// **moomoo <c>SIMULATE</c> で稼働した日だけを数える**（#334）。
/// </param>
/// <param name="TradeCount">
/// Stage 1（moomoo <c>SIMULATE</c>）で成立した取引件数。**内蔵 <c>paper</c> の擬似約定は含まない**（#334）。
/// </param>
/// <param name="ExcludedInternalPaperDays">
/// FR-20, SC-03, #334, IADR-0142 決定3: 内蔵 <c>paper</c> 稼働により**算入されなかった**営業日数。
/// <para>
/// 合格判定には用いない（<see cref="Stage1Gate"/> は参照しない）。画面が「経過 42 / 60 営業日
/// （<c>paper</c> 稼働により 3 日を除外）」と併記するための値である——**算入されなかった期間があること自体**が
/// 見えないと、進捗の数字を説明できなくなる。
/// </para>
/// </param>
public sealed record Stage1Progress(
    int QualifiedTradingDays,
    int TradeCount,
    int ExcludedInternalPaperDays = 0);

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
