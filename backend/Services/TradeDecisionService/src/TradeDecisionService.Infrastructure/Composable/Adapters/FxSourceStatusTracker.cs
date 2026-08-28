using AiStockTrading.Shared.Contracts.Events;

namespace AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;

// FR-10, FR-17, #381, ADR-0022 決定2・決定5, IADR-0196 決定1: 「いつ発行すべきか」だけを決める純粋な判定器。
//
// 🔴 **発行の判断とメッセージバスを分ける。** 一体にすると、洪水抑止の規則を確かめるのに
// Wolverine のホストを起こす必要が生じ、**最も壊れやすい部分（遷移の判定）が最も試しにくくなる。**
// 本型は状態遷移だけを持ち、外部依存を一切持たない。
internal sealed class FxSourceStatusTracker
{
    private readonly object _gate = new();

    // 通貨ごとに状態を持つ。USD が劣化していても他通貨は正常であり得る。
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);

    private sealed class State
    {
        /// <summary>非 null ＝<b>現在フォールバック中</b>。値はフォールバックを開始した時刻。</summary>
        public DateTimeOffset? FellBackAt { get; set; }

        /// <summary>
        /// 鮮度警告を最後に出した (暦日, 新規建てが止まっているか)。<b>日をまたげば再通知する</b>。
        /// <para>
        /// 🔴 <b>状態を鍵に含める</b>（#381 停止側・IADR-0198 決定2）。含めないと
        /// <b>朝に警告を出した通貨が夕方に停止域へ落ちても黙る</b>——
        /// <b>警告から停止への昇格は、同じ日に起きても知らせる必要がある。</b>
        /// </para>
        /// </summary>
        public (DateOnly Day, bool EntryBlocked)? LastStaleNotified { get; set; }

        /// <summary>
        /// 使用記録を最後に出した (源, 暦日)。<b>日をまたげば再び記録する</b>（#513・IADR-0225 決定A）。
        /// <para>
        /// 🔴 <b>源を鍵に含める。</b> フォールバック中に源が入れ替わっても（rank 2 → rank 3）遷移は起きないため、
        /// 含めないと<b>実際に使った源が台帳に残らない</b>。
        /// </para>
        /// </summary>
        public (string SourceName, DateOnly Day)? LastUsageRecorded { get; set; }
    }

    /// <summary>
    /// 源の採用を受けて、発行すべきイベントを返す（無ければ <c>null</c>）。
    /// <para>
    /// <b>遷移でのみ遷移イベントを返す。</b> 同じ状態が続く間は遷移イベントを返さない——
    /// これが「1 巡回で N 件」を防ぐ唯一の場所である。
    /// </para>
    /// <para>
    /// 🔴 <b>遷移が無い間も、暦日ごとに 1 件だけ「使用記録」を返す</b>（#513・IADR-0225 決定A）。
    /// 遷移だけでは<b>静かな期間にどの源を使ったのか台帳から証明できず</b>、日報の出典が
    /// 平常時こそ「特定できません」になっていた（IADR-0199 決定5）。抑止の鍵は <b>(通貨, 源, 暦日)</b> であり、
    /// <b>洪水は防いだまま</b>である（鮮度警告の暦日抑止〔IADR-0198 決定2〕と同じ形）。
    /// </para>
    /// <para>
    /// <b>戻り値は常に 1 件または <c>null</c> である。</b> 遷移イベントは <c>SourceName</c> を運ぶ＝
    /// <b>使用の証拠そのもの</b>であるため、遷移を発行した日は使用記録を重ねない（決定B）。
    /// </para>
    /// </summary>
    public object? OnSourceUsed(string quote, string sourceName, int rank, int totalSources, DateTimeOffset now)
    {
        lock (_gate)
        {
            var state = Get(quote);

            // 第一の源が使えている。直前までフォールバックしていたなら「回復」を発行する。
            if (rank <= 1)
            {
                var fellBackAt = state.FellBackAt;
                if (fellBackAt is null)
                {
                    return UsageIfNotYetRecorded(state, quote, sourceName, rank, totalSources, now);
                }

                state.FellBackAt = null;
                MarkUsed(state, sourceName, now);
                return new FxRateSourcePrimaryRestored(quote, sourceName, fellBackAt.Value, now);
            }

            // フォールバック中。既に発行済みなら遷移は出さない（続いていることは日報が期間で示す）。
            // ただし**使った事実は暦日ごとに残す**——フォールバック中の期間こそ出典が要る。
            if (state.FellBackAt is not null)
            {
                return UsageIfNotYetRecorded(state, quote, sourceName, rank, totalSources, now);
            }

            state.FellBackAt = now;
            MarkUsed(state, sourceName, now);
            return new FxRateSourceFellBack(quote, sourceName, rank, totalSources, now);
        }
    }

    /// <summary>当日・当該源で未記録なら使用記録を返す（記録済みなら <c>null</c>）。</summary>
    private static FxRateSourceUsed? UsageIfNotYetRecorded(
        State state, string quote, string sourceName, int rank, int totalSources, DateTimeOffset now)
    {
        var key = (SourceName: sourceName, Day: DateOnly.FromDateTime(now.UtcDateTime));
        if (state.LastUsageRecorded == key)
        {
            return null;
        }

        state.LastUsageRecorded = key;
        return new FxRateSourceUsed(quote, sourceName, rank, totalSources, now);
    }

    // 遷移イベント自身が「その源を使った」証拠になるため、同じ日の使用記録は重ねない（決定B）。
    private static void MarkUsed(State state, string sourceName, DateTimeOffset now) =>
        state.LastUsageRecorded = (sourceName, DateOnly.FromDateTime(now.UtcDateTime));

    /// <summary>
    /// 鮮度警告を受けて、発行すべきイベントを返す（当日発行済みなら <c>null</c>）。
    /// <para>
    /// 鮮度切れは<b>遷移ではなく状態</b>であり数日続く。よって遷移ではなく
    /// <b>暦日単位</b>で抑止する（IADR-0096 決定4 と同じ規律）。<b>日をまたげば再通知する</b>——
    /// 気づくための警告であり、1 度出して終わりでは役に立たない。
    /// </para>
    /// </summary>
    public FxRateStale? OnStale(
        string quote,
        DateTimeOffset asOf,
        TimeSpan age,
        TimeSpan warnThreshold,
        TimeSpan maxAge,
        DateTimeOffset now,
        bool entryBlocked = false)
    {
        var key = (Day: DateOnly.FromDateTime(now.UtcDateTime), EntryBlocked: entryBlocked);

        lock (_gate)
        {
            var state = Get(quote);
            if (state.LastStaleNotified == key)
            {
                return null;
            }

            state.LastStaleNotified = key;
            return new FxRateStale(
                quote, asOf, age.TotalDays, warnThreshold.TotalDays, maxAge.TotalDays, now, entryBlocked);
        }
    }

    /// <summary>
    /// 発行に失敗した分の状態を戻す。<b>恒久的に握り潰さない</b>——
    /// 戻さないと「発行済み」として記録され、<b>次の機会にも二度と出なくなる</b>
    /// （IADR-0096 の巻き戻しと同じ理由）。
    /// <para>
    /// 🔴 <b>使用記録の印は「多めに出る」側へ倒して消す</b>（#513・IADR-0225 決定C）。
    /// 消しすぎても<b>当日に使用記録が 1 件余分に出るだけ</b>だが、消し損ねると
    /// <b>その日にどの源を使ったかの証拠が永久に欠ける</b>。<b>欠測より重複を採る。</b>
    /// </para>
    /// </summary>
    public void Rollback(string quote, object published)
    {
        lock (_gate)
        {
            var state = Get(quote);
            switch (published)
            {
                case FxRateSourceFellBack:
                    state.FellBackAt = null;
                    state.LastUsageRecorded = null;
                    break;
                case FxRateSourcePrimaryRestored restored:
                    state.FellBackAt = restored.FellBackAt;
                    state.LastUsageRecorded = null;
                    break;
                case FxRateSourceUsed:
                    state.LastUsageRecorded = null;
                    break;
                case FxRateStale:
                    state.LastStaleNotified = null;
                    break;
            }
        }
    }

    private State Get(string quote)
    {
        if (!_states.TryGetValue(quote, out var state))
        {
            state = new State();
            _states[quote] = state;
        }

        return state;
    }
}
