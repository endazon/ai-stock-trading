using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-20, FR-11, #387, 06_daytrading-review §4.1 条件1, IADR-0148:
// Stage 1 → Stage 2 の合格条件「統制違反 0 件」（**クラス C 限定**）の集計。
//
// **段階ゲートの入力のうち、0 が fail-safe にならない唯一の値である。** 営業日数・取引件数の 0 は
// 「条件未充足＝昇格しない」に倒れるが、違反件数の 0 は「違反が無い＝条件充足」を意味する。
// よって**「集計が供給されていない」状態を 0 と同じ形で表現してはならない**——供給元が無いまま
// #385 / #386 が期間・件数を供給した瞬間、その 0 が無条件で条件1 を通す。
//
// 本ファイルは「未供給」を 0 とは別の形（`ControlViolationTally?` の `null`）で表し、
// 判定側（StageGate）が両者を必ず区別することを型で強いる。

/// <summary>
/// FR-20, #387, IADR-0148 決定1: <b>供給された</b>クラス C 統制違反の件数。
/// <para>
/// <b>この型の値が存在すること自体が「集計が供給された」ことを意味する。</b>
/// 未供給は <c>null</c> であり、<c>Observed(0)</c>（＝観測した結果 0 件だった）とは別物である。
/// 両者を同じ <c>int</c> の 0 で表すと、供給元の不在が「合格」として読まれる（#387 の fail-open そのもの）。
/// </para>
/// <para>
/// <b>計上単位は 1 回の発注拒否につき 1 件</b>である（1 回の拒否で複数のクラス C 理由が返っても 1 件。
/// 06_daytrading-review §4.1）。単位の担保は <see cref="ControlViolationAggregation"/> と
/// 観測ログの主キー（<c>DecisionId</c>）が行う。
/// </para>
/// </summary>
public sealed record ControlViolationTally
{
    /// <param name="count">観測されたクラス C 統制違反の件数（0 以上）。</param>
    public ControlViolationTally(int count)
    {
        // 負の件数は観測結果として成立しない（供給側の異常を黙って通さない）。
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Count = count;
    }

    /// <summary>観測されたクラス C 統制違反の件数。</summary>
    public int Count { get; }

    /// <summary>観測結果としての件数を作る。<b>0 件を主張するのは、実際に観測できたときだけである。</b></summary>
    public static ControlViolationTally Observed(int count) => new(count);

    /// <summary>
    /// 合格条件「統制違反 0 件」を満たすか（§4.1 条件1）。
    /// <para>
    /// 判定を型の側に持たせるのは、<c>tally?.Count &gt; 0</c> のような書き方が
    /// <b>未供給（null）を「違反なし」へ黙って倒す</b>ためである（C# の持ち上げ比較は null で false を返す）。
    /// 呼び出し側は「未供給か」を先に判定しなければ本プロパティへ到達できない。
    /// </para>
    /// </summary>
    public bool BlocksPromotion => Count > 0;
}

/// <summary>
/// FR-20, FR-11, #387, IADR-0148 決定3: 発注審査 1 回ぶんの観測。
/// <para>
/// <b>承認された審査も観測する。</b>記録するのが違反だけだと「違反 0 件」を主張する根拠が無く、
/// 未供給と区別できない。算入対象の発注先での審査が 1 件でも記録されていれば「集計は供給されている」とし、
/// そのうちクラス C を含む拒否の件数を数える。
/// </para>
/// </summary>
/// <param name="DecisionId">
/// 取引判断の ID。<b>計上単位（1 回の発注拒否につき 1 件）を担保する鍵</b>であり、観測ログの主キーでもある
/// （再送で二重計上しない）。
/// </param>
/// <param name="Provider">
/// FR-20, #334, IADR-0142 決定1: その審査が向いていた<b>発注先</b>。<b>既定値を与えない</b>——
/// 省略できるようにすると書き忘れが「算入される側」へ倒れる。
/// </param>
/// <param name="RejectionReasons">
/// その審査で返った拒否理由の全件（承認なら空）。<b>分類は行わない</b>——
/// クラス分けの単一情報源は <see cref="RejectionReasonClassification"/> である。
/// </param>
public sealed record OrderScreeningObservation(
    Guid DecisionId,
    BrokerProvider Provider,
    IReadOnlyList<RejectionReason> RejectionReasons);

/// <summary>
/// FR-20, FR-11, #387, 06_daytrading-review §4.1 条件1, IADR-0148: 発注審査の観測からクラス C 統制違反を数える（純関数）。
/// </summary>
public static class ControlViolationAggregation
{
    /// <summary>
    /// その発注先の審査を Stage 1 の合格判定へ算入してよいか。
    /// <para>
    /// 判定は <see cref="Stage1Aggregation.IsCounted"/> に委ねる（<c>MoomooSimulate</c> の**許可制**・IADR-0142 決定2）。
    /// FR-20 は「経過営業日数・取引件数・<b>統制違反件数</b>のいずれも <c>SIMULATE</c> の約定のみで数え」ると定める。
    /// 3 指標で算入規則が食い違うと、同じ「Stage 1 の実績」という言葉が指すものが指標ごとに変わる。
    /// </para>
    /// </summary>
    public static bool IsCounted(BrokerProvider provider) => Stage1Aggregation.IsCounted(provider);

    /// <summary>
    /// その審査を統制違反 1 件として数えるか。
    /// <para>
    /// 算入対象の発注先であり、かつ<b>クラス C の理由を 1 つ以上含む拒否</b>であること。
    /// クラス分けは <see cref="RejectionReasonClassification"/> に委ねる（**再実装しない**）。
    /// </para>
    /// </summary>
    public static bool CountsAsViolation(OrderScreeningObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return IsCounted(observation.Provider)
            && RejectionReasonClassification.CountsAsControlViolation(observation.RejectionReasons);
    }

    /// <summary>
    /// 観測の並びから件数を集計する。
    /// <para>
    /// <b>算入対象の観測が 1 件も無ければ <c>null</c>（未供給）を返す。</b>「0 件だった」と
    /// 「そもそも数えていない」を同じ 0 で返さないことが本メソッドの要点である。
    /// </para>
    /// <para>
    /// 同一 <c>DecisionId</c> の重複は 1 件として数える（1 回の発注拒否につき 1 件）。
    /// </para>
    /// </summary>
    public static ControlViolationTally? Tally(IEnumerable<OrderScreeningObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        var counted = observations.Where(o => IsCounted(o.Provider)).ToList();
        if (counted.Count == 0)
        {
            return null;
        }

        var violations = counted
            .Where(CountsAsViolation)
            .Select(o => o.DecisionId)
            .Distinct()
            .Count();
        return ControlViolationTally.Observed(violations);
    }
}
