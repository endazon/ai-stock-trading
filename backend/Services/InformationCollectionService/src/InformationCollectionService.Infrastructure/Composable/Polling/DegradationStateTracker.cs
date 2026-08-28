using InformationCollectionService.Domain;
using AiStockTrading.Shared.Contracts.Events;

namespace InformationCollectionService.Infrastructure.Polling;

// FR-01, FR-09, FR-11, #336, ADR-0020 決定2-3: 「いつイベントを発行すべきか」だけを決める純粋な判定器。
//
// 🔴 **発行の判断とメッセージバスを分ける**（FX の FxSourceStatusTracker と同じ形）。一体にすると、
// 洪水抑止の規則を確かめるのに Wolverine のホストを起こす必要が生じ、**最も壊れやすい部分が最も試しにくくなる。**
//
// 🔴 **該当サイクル数を数えるのはここである。** 縮退が続いている間の巡回数を数え、回復時に載せる
// （受け手に引き算・数え直しをさせない）。
internal sealed class DegradationStateTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Outage> _outages = new(StringComparer.Ordinal);

    private sealed record Outage(DateTimeOffset DegradedAt)
    {
        public int AffectedCycles { get; set; } = 1;
    }

    /// <summary>
    /// 1 巡回の判定結果を受けて、発行すべきイベント（0〜N 件）を返す。
    /// <b>遷移でのみ返す</b>——同じ状態が続く間は空を返し、サイクル数だけを数える。
    /// </summary>
    public IReadOnlyList<object> Observe(CollectionDegradation degradation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(degradation);

        // 現在欠測しているカテゴリ（ニュース系はまとまりで 1 件、その他はソース名を鍵にする）。
        var degradedNow = new Dictionary<string, (string Behavior, List<string> Sources, bool BlocksNewEntries)>(
            StringComparer.Ordinal);

        if (degradation.NewsOutage)
        {
            degradedNow[InformationSourceCatalog.NewsCategory] =
                (nameof(MissingSourceBehavior.LimitedDegradation),
                 [.. degradation.MissingRequired.Where(IsNewsSource)],
                 true);
        }

        foreach (var source in degradation.MissingRequired.Where(s => !IsNewsSource(s)))
        {
            var behavior = degradation.AbortCycle
                ? nameof(MissingSourceBehavior.AbortCycle)
                : nameof(MissingSourceBehavior.RecordAndNotifyOnly);
            degradedNow[source] = (behavior, [source], degradation.BlocksNewEntries);
        }

        var events = new List<object>();

        lock (_gate)
        {
            foreach (var (category, state) in degradedNow)
            {
                if (_outages.TryGetValue(category, out var existing))
                {
                    // 継続中。黙るが、該当サイクル数は数える。
                    existing.AffectedCycles++;
                    continue;
                }

                _outages[category] = new Outage(now);
                events.Add(new InformationSourceDegraded(
                    category, state.Behavior, state.Sources, state.BlocksNewEntries, now));
            }

            foreach (var category in _outages.Keys.Where(k => !degradedNow.ContainsKey(k)).ToList())
            {
                var outage = _outages[category];
                _outages.Remove(category);
                events.Add(new InformationSourceRecovered(
                    category, outage.DegradedAt, outage.AffectedCycles, now));
            }
        }

        return events;
    }

    /// <summary>
    /// 発行に失敗した分の状態を戻す。<b>恒久的に握り潰さない</b>——
    /// 戻さないと「発行済み」として記録され、<b>次の機会にも二度と出なくなる</b>（IADR-0196 と同じ理由）。
    /// </summary>
    public void Rollback(object published)
    {
        lock (_gate)
        {
            switch (published)
            {
                case InformationSourceDegraded degraded:
                    _outages.Remove(degraded.Category);
                    break;
                case InformationSourceRecovered recovered:
                    _outages[recovered.Category] = new Outage(recovered.DegradedAt)
                    {
                        AffectedCycles = recovered.AffectedCycles,
                    };
                    break;
            }
        }
    }

    private static bool IsNewsSource(string name) =>
        InformationSourceCatalog.Default.Find(name) is { } d
        && string.Equals(d.Category, InformationSourceCatalog.NewsCategory, StringComparison.OrdinalIgnoreCase);
}
