using InformationCollectionService.Domain;
using AiStockTrading.Shared.Contracts.Events;

namespace InformationCollectionService.Hosted;

// FR-01, FR-09, FR-11, #336, ADR-0020 決定2-3: 「いつイベントを発行すべきか」だけを決める純粋な判定器。
//
// 🔴 **発行の判断とメッセージバスを分ける**（FX の FxSourceStatusTracker と同じ形）。一体にすると、
// 洪水抑止の規則を確かめるのに Wolverine のホストを起こす必要が生じ、**最も壊れやすい部分が最も試しにくくなる。**
//
// 🔴 **該当サイクル数を数えるのはここである。** 縮退が続いている間の巡回数を数え、回復時に載せる
// （受け手に引き算・数え直しをさせない）。
//
// FR-10, #564, IADR-0267: 🔴 **遷移に加えて「現況」を毎巡回 1 件出す。** 遷移だけでは
// **縮退が続く静かな区間に受け手（リスク管理）が再起動すると停止が復元されない**（fail-open）。
// 現況観測（InformationSourceStateObserved）は**抑止しない**——抑止＝鮮度の喪失であり、
// 本イベントは鮮度そのものが統制の入力だからである（#513 の暦日抑止と目的が違う）。
public sealed class DegradationStateTracker(TimeSpan observationValidity)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Outage> _outages = new(StringComparer.Ordinal);

    private sealed record Outage(DateTimeOffset DegradedAt)
    {
        public int AffectedCycles { get; set; } = 1;
    }

    /// <summary>
    /// 1 巡回の判定結果を受けて、発行すべきイベント（1〜N 件）を返す。
    /// <para>
    /// <b>遷移イベントは遷移でのみ返す</b>——同じ状態が続く間は返さず、サイクル数だけを数える。
    /// </para>
    /// <para>
    /// 🔴 <b>現況観測（<c>InformationSourceStateObserved</c>）は毎巡回 1 件を必ず返す</b>（#564）。
    /// 遷移だけでは<b>静かな区間に受け手が状態を引けない</b>ため、
    /// <b>いま止めているカテゴリの全量</b>を言い直す。<b>縮退が無い巡回でも空集合の観測を返す</b>——
    /// 「観測して健全だった」と「まだ何も聞いていない」を受け手が区別できるようにするためである。
    /// </para>
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

        // #564: 現況の全量。**遷移の有無にかかわらず** 1 件出す。順序は安定させる（台帳・テストの読みやすさ）。
        // 載せるのは BlocksNewEntries=true のカテゴリだけであり、受け手が Behavior を再解釈する余地を残さない。
        events.Add(new InformationSourceStateObserved(
            [.. degradedNow.Where(e => e.Value.BlocksNewEntries).Select(e => e.Key).Order(StringComparer.Ordinal)],
            observationValidity,
            now));

        return events;
    }

    /// <summary>
    /// 発行に失敗した分の状態を戻す。<b>恒久的に握り潰さない</b>——
    /// 戻さないと「発行済み」として記録され、<b>次の機会にも二度と出なくなる</b>（IADR-0196 と同じ理由）。
    /// <para>
    /// <b>現況観測（<c>InformationSourceStateObserved</c>）には戻す状態が無い</b>（抑止していないため）。
    /// 失敗しても<b>次の巡回で同じ現況が再び出る</b>——それが定期発行にした理由である。
    /// </para>
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
