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

        /// <summary>鮮度警告を最後に出した暦日（UTC）。<b>日をまたげば再通知する</b>。</summary>
        public DateOnly? LastStaleNotifiedDay { get; set; }
    }

    /// <summary>
    /// 源の採用を受けて、発行すべきイベントを返す（無ければ <c>null</c>）。
    /// <para>
    /// <b>遷移でのみイベントを返す。</b> 同じ状態が続く間は <c>null</c> を返し続ける——
    /// これが「1 巡回で N 件」を防ぐ唯一の場所である。
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
                    return null;
                }

                state.FellBackAt = null;
                return new FxRateSourcePrimaryRestored(quote, sourceName, fellBackAt.Value, now);
            }

            // フォールバック中。既に発行済みなら黙る（続いていることは日報が期間で示す）。
            if (state.FellBackAt is not null)
            {
                return null;
            }

            state.FellBackAt = now;
            return new FxRateSourceFellBack(quote, sourceName, rank, totalSources, now);
        }
    }

    /// <summary>
    /// 鮮度警告を受けて、発行すべきイベントを返す（当日発行済みなら <c>null</c>）。
    /// <para>
    /// 鮮度切れは<b>遷移ではなく状態</b>であり数日続く。よって遷移ではなく
    /// <b>暦日単位</b>で抑止する（IADR-0096 決定4 と同じ規律）。<b>日をまたげば再通知する</b>——
    /// 気づくための警告であり、1 度出して終わりでは役に立たない。
    /// </para>
    /// </summary>
    public FxRateStale? OnStale(
        string quote, DateTimeOffset asOf, TimeSpan age, TimeSpan warnThreshold, TimeSpan maxAge, DateTimeOffset now)
    {
        var day = DateOnly.FromDateTime(now.UtcDateTime);

        lock (_gate)
        {
            var state = Get(quote);
            if (state.LastStaleNotifiedDay == day)
            {
                return null;
            }

            state.LastStaleNotifiedDay = day;
            return new FxRateStale(
                quote, asOf, age.TotalDays, warnThreshold.TotalDays, maxAge.TotalDays, now);
        }
    }

    /// <summary>
    /// 発行に失敗した分の状態を戻す。<b>恒久的に握り潰さない</b>——
    /// 戻さないと「発行済み」として記録され、<b>次の機会にも二度と出なくなる</b>
    /// （IADR-0096 の巻き戻しと同じ理由）。
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
                    break;
                case FxRateSourcePrimaryRestored restored:
                    state.FellBackAt = restored.FellBackAt;
                    break;
                case FxRateStale:
                    state.LastStaleNotifiedDay = null;
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
