using System.Text.RegularExpressions;
using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Domain;

// FR-13, FR-14, ADR-0042 決定 1・2, #1025, IADR-0433 決定 2: `/policy` の監視銘柄の入れ替え案を適用するかの判定（純関数）。
//
// 🔴 **案を作った時点の監視銘柄（期待値）と現在が違えば、1 件も適用しない**（ADR-0042 決定 1「楽観排他」）。
// 🔴 **各銘柄は SC-02 と同じ検証を通す**（追加の重複・除外の不在は適用しない。`MonitorWatchlistService.Add/Remove` と同じ規則）。
// 通らない銘柄は「適用しない」と理由つきで返し、通った銘柄だけを 1 回の保存で適用する（一部適用の内訳は呼び出し側が報告する）。
// 案の形（件数・書式・理由）の違反は要求そのものの誤りであり、1 件も適用しない（呼び出し側が 400）。
public static partial class WatchlistProposalPlan
{
    public const int MaxAdditions = 5;
    public const int MaxRemovals = 5;
    public const int MaxReasonLength = 200;

    // 報告書サービスの検証（PolicyRevisionProposalParser）と同じ米国のティッカー書式。
    [GeneratedRegex(@"\A[A-Z]{1,5}([.-][A-Z]{1,2})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex UsTicker();

    /// <summary>案の形を検証する。違反があれば理由（400 で返す文）、無ければ null。</summary>
    public static string? ValidateShape(IReadOnlyList<ProposedWatchlistChange>? changes, IReadOnlyList<MonitoredSymbol>? expected)
    {
        if (expected is null)
            return "案を作った時点の監視銘柄（expectedWatchlist）が必要です。";
        if (changes is null || changes.Count == 0)
            return "適用する入れ替え（changes）がありません。";
        if (changes.Count(c => c.Action == ProposedWatchlistAction.Add) > MaxAdditions)
            return $"追加は {MaxAdditions} 件までです。";
        if (changes.Count(c => c.Action == ProposedWatchlistAction.Remove) > MaxRemovals)
            return $"除外は {MaxRemovals} 件までです。";
        if (changes.Any(c => !UsTicker().IsMatch(c.Symbol ?? string.Empty)))
            return "銘柄は米国のティッカー（大文字。例 AAPL / BRK.B）に限ります。";
        if (changes.Any(c => string.IsNullOrWhiteSpace(c.Reason) || c.Reason.Length > MaxReasonLength))
            return $"理由は必須で {MaxReasonLength} 文字までです。";
        if (changes.Select(c => c.Symbol.ToUpperInvariant()).Distinct().Count() != changes.Count)
            return "同じ銘柄が重複しています。";
        return null;
    }

    /// <summary>
    /// 現在の監視銘柄に案を当てた結果（適用後の一覧と銘柄ごとの結果）。期待値と違えば <see cref="WatchlistApplyPlan.Stale"/>。
    /// </summary>
    /// <remarks>
    /// FR-13, ADR-0043（計画）決定 2 (b)・4, #1030, IADR-0437: <paramref name="cycleFit"/> があれば、追加は「1 巡回が巡回間隔に
    /// 収まること」を満たす範囲だけ適用し、満たさない追加は理由つきで適用しない。<b>除外はこの検査で止めない</b>（予算を減らす向き）。
    /// 除外を先に当ててから追加を案の順に当てる（同じ案の除外で空いた枠を追加に使う）。案の中で同じ銘柄は重複しない
    /// （<see cref="ValidateShape"/>）ため、当てる順は重複・不在の判定に影響しない。内訳は案の順のまま返す。
    /// </remarks>
    public static WatchlistApplyPlan Plan(
        IReadOnlyCollection<MonitoredSymbol> current,
        IReadOnlyList<MonitoredSymbol> expected,
        IReadOnlyList<ProposedWatchlistChange> changes,
        WatchlistCycleFit? cycleFit = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(changes);

        if (!SameSet(current, expected))
            return new WatchlistApplyPlan(Stale: true, current, []);

        var working = current.ToList();
        var items = new WatchlistApplyItem[changes.Count];

        // 1 周目: 除外（検査で止めない）。
        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            if (change.Action != ProposedWatchlistAction.Remove)
                continue;

            var target = new MonitoredSymbol(change.Symbol, Market.UnitedStates);
            if (!working.Any(s => Same(s, target)))
            {
                items[i] = new(change, Applied: false, $"銘柄 {change.Symbol} は監視対象にありません");
                continue;
            }

            working.RemoveAll(s => Same(s, target));
            items[i] = new(change, Applied: true, null);
        }

        // 2 周目: 追加（案の順。1 巡回に収まらなくなる追加は適用しない）。
        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            if (change.Action != ProposedWatchlistAction.Add)
                continue;

            var target = new MonitoredSymbol(change.Symbol, Market.UnitedStates);
            if (working.Any(s => Same(s, target)))
            {
                items[i] = new(change, Applied: false, $"銘柄 {change.Symbol} は既に監視対象です");
                continue;
            }

            List<MonitoredSymbol> after = [.. working, target];
            if (cycleFit is not null && cycleFit.Refuses(target, after))
            {
                items[i] = new(change, Applied: false, $"Finnhub の巡回に収まりません（{cycleFit.Describe(after)}）");
                continue;
            }

            working.Add(target);
            items[i] = new(change, Applied: true, null);
        }

        return new WatchlistApplyPlan(Stale: false, working, items);
    }

    // 集合として同じか（銘柄コードは大文字小文字を無視・市場は一致。並び・重複は問わない）。
    private static bool SameSet(IReadOnlyCollection<MonitoredSymbol> a, IReadOnlyCollection<MonitoredSymbol> b) =>
        a.Select(Key).ToHashSet().SetEquals(b.Select(Key));

    private static (string, Market) Key(MonitoredSymbol s) => (s.Symbol.Trim().ToUpperInvariant(), s.Market);

    // `MonitorWatchlistService.Same` と同じ突き合わせ。
    private static bool Same(MonitoredSymbol a, MonitoredSymbol b) =>
        a.Market == b.Market && string.Equals(a.Symbol, b.Symbol, StringComparison.OrdinalIgnoreCase);
}

public enum ProposedWatchlistAction
{
    Add,
    Remove,
}

// 案の 1 件（市場は米国に限る＝案の検証が米国のティッカーだけを通すため）。
public sealed record ProposedWatchlistChange(ProposedWatchlistAction Action, string Symbol, string Reason);

// 銘柄ごとの結果。Applied=false なら SkipReason に理由。
public sealed record WatchlistApplyItem(ProposedWatchlistChange Change, bool Applied, string? SkipReason);

// 適用の計画。Stale=true なら 1 件も適用しない（案の作成後に監視銘柄が変わった）。
public sealed record WatchlistApplyPlan(
    bool Stale,
    IReadOnlyCollection<MonitoredSymbol> Resulting,
    IReadOnlyList<WatchlistApplyItem> Items)
{
    public bool AnyApplied => Items.Any(i => i.Applied);
}
