using AiStockTrading.Shared.Contracts.Trading;
using MarketMonitorService.Domain;

namespace MarketMonitorService.Features.MarketMonitor.GetWatchlistAsOf;

/// <summary>
/// FR-04, FR-13, FR-15, ADR-0044 決定 3, ADR-0046 決定 1, #1049, IADR-0442 決定 1: 当時の監視銘柄の照会（<c>GET /monitor/watchlist/as-of</c>）の応答。
/// <para>
/// 🔴 <b>再構成できたかを明示の真偽で持つ</b>（原則 A）。「再構成できない」を 404 や空の一覧で表すと、受け手が「当時 0 件だった」と読み得る。
/// 応答は常に 200 である。
/// </para>
/// </summary>
/// <param name="Reconstructed">当時の一覧を再構成できたか。</param>
/// <param name="Symbols">再構成できた一覧（0 件もあり得る＝当時 0 件だったという事実）。できないときは null。</param>
/// <param name="Basis">何から再構成したか（<see cref="Bases"/>）。できないときは null。</param>
/// <param name="BasisChangedAt">再構成に使った変更の時刻（変更が無いときは null）。</param>
/// <param name="SeededAt">
/// 監視設定の行の seed の時刻（IADR-0282 決定 2）。再構成できたか否かに関わらず返す（ADR-0046 決定 1「供給口は SeededAt も返す」）。
/// null は「記録されていない」。🔴 本項目は読み取り専用の供給口の応答にだけ載せ、利用者が変えられる API（設定の照会・BFF）には出さない。
/// </param>
/// <param name="Reason">再構成できないときの理由（人が読む文）。</param>
public sealed record WatchlistAsOfResponse(
    bool Reconstructed,
    IReadOnlyList<MonitoredSymbol>? Symbols,
    string? Basis,
    DateTimeOffset? BasisChangedAt,
    DateTimeOffset? SeededAt,
    string? Reason)
{
    /// <summary>再構成の根拠の語彙。</summary>
    public static class Bases
    {
        /// <summary>その時点以前で最後の変更の「変更後」（ADR-0044 決定 3）。</summary>
        public const string AfterChange = "after-change";

        /// <summary>SeededAt 以降・最初の変更より前の時点での、最初の変更の「変更前」（ADR-0046 決定 1）。</summary>
        public const string BeforeFirstChange = "before-first-change";

        /// <summary>変更が 1 件も無いときの、SeededAt 以降の現在の一覧（＝ seed の一覧。ADR-0046 決定 1）。</summary>
        public const string SeedWithoutChanges = "seed-without-changes";
    }
}

/// <summary>
/// FR-04, FR-13, FR-15, ADR-0044 決定 3, ADR-0046 決定 1・2, #1049, IADR-0442 決定 1: 市場監視の変更履歴と seed の時刻から、
/// 指定した時刻に有効だった監視銘柄を再構成する<b>純関数</b>（I/O を持たない）。
/// <para>規則（IADR-0442 決定 1）:</para>
/// <list type="number">
/// <item>監視銘柄の変更（追加・削除）の行だけを使う。変動閾値・クールダウンの行は無視する。同じ時刻の行は 1 つの組として扱う。</item>
/// <item>変更が 1 件も無い: <c>SeededAt</c> 以降は現在の一覧。行が無い・<c>SeededAt</c> が null・<c>SeededAt</c> より前は再構成できない。</item>
/// <item>最初の変更より前: <c>SeededAt</c> 以降（境界を含む）に限り最初の変更の「変更前」。<c>SeededAt</c> が null・最初の変更より後（矛盾）・
/// <c>SeededAt</c> より前は再構成できない。🔴 <c>SeededAt</c> を推測で埋めない（ADR-0046 決定 2）。</item>
/// <item>それ以外: その時刻以前（<b>同時刻を含む</b>）で最後の変更の「変更後」。</item>
/// <item>🔴 一貫性の検査（原則 A の側の厳しい読み。除外を増やすだけで、誤って合格させる方向には働かない）: 使う組の前後値が食い違う・
/// 次の変更の「変更前」と合わない・最後の変更の「変更後」が現在の一覧と合わない・前後値を一覧へ戻せない・未来の時点は、再構成できない。</item>
/// </list>
/// </summary>
public static class WatchlistAsOfReconstructor
{
    // 前後値の描画（`MonitorWatchlistService.Render`・`MonitorSettingsService.RenderSymbols` と同じ書式）。
    // 🔴 書式が変われば読み戻しの往復検査（TryParse）が食い違いを「再構成できない」へ倒す（黙って別の一覧を返さない）。
    internal const string EmptyRendering = "(なし)";

    public static WatchlistAsOfResponse Reconstruct(
        DateTimeOffset at,
        DateTimeOffset now,
        IReadOnlyList<MonitorSettingsChangeEntry> history,
        MonitorSeedState? seed)
    {
        ArgumentNullException.ThrowIfNull(history);

        var seededAt = seed?.SeededAt;
        WatchlistAsOfResponse Unreconstructable(string reason) => new(false, null, null, null, seededAt, reason);

        if (at > now)
            return Unreconstructable("未来の時点です（その時点の監視銘柄は分かりません）。");

        var groups = history
            .Where(e => e.ChangeType is MonitorSettingsChangeType.WatchlistSymbolAdded
                or MonitorSettingsChangeType.WatchlistSymbolRemoved)
            .GroupBy(e => e.ChangedAt)
            .OrderBy(g => g.Key)
            .Select(g => (At: g.Key, Entries: g.ToList()))
            .ToList();

        // ---- 変更が 1 件も無い（ADR-0046 決定 1） ----
        if (groups.Count == 0)
        {
            if (seed is null)
                return Unreconstructable("監視設定の行がありません（seed がまだ一度も適用されていません）。");
            if (seededAt is not { } seeded)
                return Unreconstructable(NoSeededAtReason);
            if (at < seeded)
                return Unreconstructable("SeededAt より前の時点です（その時点の監視銘柄は無かったか空でした）。");

            return new WatchlistAsOfResponse(
                true, seed.CurrentSymbols, WatchlistAsOfResponse.Bases.SeedWithoutChanges, null, seededAt, null);
        }

        // ---- 最初の変更より前（ADR-0046 決定 1） ----
        var first = groups[0];
        if (at < first.At)
        {
            if (seededAt is not { } seeded)
                return Unreconstructable(NoSeededAtReason);
            if (seeded > first.At)
                return Unreconstructable("SeededAt が最初の変更より後にあり、記録が矛盾しています。");
            if (at < seeded)
                return Unreconstructable("SeededAt より前の時点です（その時点の監視銘柄は無かったか空でした）。");
            if (!TrySingle(first.Entries.Select(e => e.Before), out var before))
                return Unreconstructable("最初の変更と同じ時刻の行で、変更前の一覧が食い違っています（どれが先か決められません）。");
            if (!TryParse(before, out var beforeList))
                return Unreconstructable("最初の変更の変更前の一覧を読み戻せません。");

            return new WatchlistAsOfResponse(
                true, beforeList, WatchlistAsOfResponse.Bases.BeforeFirstChange, first.At, seededAt, null);
        }

        // ---- その時刻以前で最後の変更の変更後（ADR-0044 決定 3。同時刻を含む） ----
        var index = groups.FindLastIndex(g => g.At <= at);
        var chosen = groups[index];
        if (!TrySingle(chosen.Entries.Select(e => e.After), out var after))
            return Unreconstructable("同じ時刻の行で、変更後の一覧が食い違っています（どれが最後か決められません）。");

        if (index + 1 < groups.Count)
        {
            // 連続性: 次の変更は、選んだ変更後の一覧から始まっていなければならない。
            var next = groups[index + 1];
            if (!next.Entries.Any(e => e.Before == after))
                return Unreconstructable("選んだ変更の変更後が、次の変更の変更前と一致しません（履歴の外で一覧が変わった兆候）。");
        }
        else
        {
            // 最後の変更の後: 現在の一覧が、その変更後のままでなければならない。
            if (seed is null)
                return Unreconstructable("監視設定の行がありません（最後の変更の後の一覧を確かめられません）。");
            if (Render(seed.CurrentSymbols) != after)
                return Unreconstructable("最後の変更の変更後が現在の一覧と一致しません（履歴の外で一覧が変わった兆候）。");
        }

        if (!TryParse(after, out var afterList))
            return Unreconstructable("変更後の一覧を読み戻せません。");

        return new WatchlistAsOfResponse(
            true, afterList, WatchlistAsOfResponse.Bases.AfterChange, chosen.At, seededAt, null);
    }

    private const string NoSeededAtReason =
        "SeededAt が記録されていません（2026-09-02 の移行より前に作られた行）。seed の時刻は推測で埋めません。";

    // 組の値がただ 1 つ（null でない）なら返す。
    private static bool TrySingle(IEnumerable<string?> values, out string value)
    {
        var distinct = values.Distinct(StringComparer.Ordinal).ToList();
        if (distinct is [{ } only])
        {
            value = only;
            return true;
        }

        value = string.Empty;
        return false;
    }

    // `SYMBOL@Market, SYMBOL@Market` / `(なし)` を一覧へ戻す。🔴 銘柄に区切り（@・,）を含む要素と、戻した一覧を描き直して元の文字列と
    // 一致しないものは失敗とする（別の一覧として読めてしまう形を通さない）。
    internal static bool TryParse(string rendering, out IReadOnlyList<MonitoredSymbol> symbols)
    {
        symbols = [];
        if (rendering == EmptyRendering)
            return true;

        var list = new List<MonitoredSymbol>();
        foreach (var item in rendering.Split(", "))
        {
            var at = item.LastIndexOf('@');
            if (at <= 0 || at == item.Length - 1)
                return false;

            var marketName = item[(at + 1)..];
            if (char.IsDigit(marketName[0]) || marketName[0] == '-'
                || !Enum.TryParse<Market>(marketName, ignoreCase: false, out var market) || !Enum.IsDefined(market))
            {
                return false;
            }

            // 銘柄に区切り（@・,）が入っていると、別の一覧としても読めてしまう。曖昧なものは戻さない。
            var symbol = item[..at];
            if (symbol.Contains('@') || symbol.Contains(','))
                return false;

            list.Add(new MonitoredSymbol(symbol, market));
        }

        if (Render(list) != rendering)
            return false;

        symbols = list;
        return true;
    }

    internal static string Render(IReadOnlyCollection<MonitoredSymbol> symbols) =>
        symbols.Count == 0 ? EmptyRendering : string.Join(", ", symbols.Select(s => $"{s.Symbol}@{s.Market}"));
}
