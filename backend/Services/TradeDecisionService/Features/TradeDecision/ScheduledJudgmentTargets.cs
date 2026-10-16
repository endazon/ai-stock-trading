using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision;

// 🔴 FR-02, FR-04, UC-01, ADR-0051, #1286, IADR-0521 決定 1・2: 定時サイクルの判断対象（監視銘柄 ∪ 保有中の銘柄）。
//   - 監視銘柄に在る保有銘柄は出口専用にしない（従来どおり新規建ても判断できる）。監視銘柄に無い保有銘柄（保有のみ）は出口専用（ExitOnly）にする。
//   - 照合は監視銘柄の所属の判定（TradeDecisionPromptBuilder.WatchlistSection）と同じく、市場が一致し、銘柄は前後の空白を除いて
//     大文字小文字を区別しない。保有の重複は 1 件にまとめる。監視銘柄に在る保有銘柄は監視銘柄の側の表記で出す。
//   - held が null（不明）なら監視銘柄だけを返す（従来の巡回と同じ）。
//   - 🔴 IADR-0521 決定 4: excludedHeldOnly（全注文が審査で必ず拒否される保有のみの銘柄＝市場の無効・禁止銘柄）は足さない。
//     決済も必ず拒否されるため、判断しても LLM の費用と拒否・通知の繰り返しにしかならない。監視銘柄からは外さない（従来どおり）。
// 🔴 FR-02, FR-04, #1367, IADR-0546 決定 1・2: **保有中の銘柄（監視銘柄に在るものも含む）を先頭に**、保有の応答の順で並べ、
//   その後ろに保有していない監視銘柄を監視銘柄の順で並べる。判断は直列なので、並びがそのまま出口判断の遅れになる
//   （旧い並び〔保有のみが末尾〕では開場直後の −1.5% 撤退が約 4 分遅れた）。サイクルが実行時間の上限で打ち切られたときに
//   判断されずに残るのは**末尾の保有していない監視銘柄**であり、出口判断を後回しにしない。
public static class ScheduledJudgmentTargets
{
    public static IReadOnlyList<(WatchedSymbol Symbol, bool ExitOnly)> Build(
        IReadOnlyList<WatchedSymbol> watchlist,
        IReadOnlyList<WatchedSymbol>? held,
        IReadOnlySet<WatchedSymbol>? excludedHeldOnly = null)
    {
        ArgumentNullException.ThrowIfNull(watchlist);

        if (held is null)
            return watchlist.Select(w => (w, false)).ToList();

        // 監視銘柄の表記（最初の 1 件）。監視銘柄に在る保有銘柄は、こちらの表記で出す（DecisionId・プロンプトの「判断対象」は従来と同じ）。
        var watched = new Dictionary<(string, Market), WatchedSymbol>();
        foreach (var w in watchlist)
            watched.TryAdd(Key(w), w);

        var targets = new List<(WatchedSymbol, bool)>(watchlist.Count + held.Count);
        var placed = new HashSet<(string, Market)>();

        // IADR-0546 決定 1・2: 保有中の銘柄を先頭に、保有の応答の順で。
        foreach (var h in held)
        {
            var key = Key(h);
            if (watched.TryGetValue(key, out var w))
            {
                if (placed.Add(key))
                    targets.Add((w, false));
                continue;
            }

            if (excludedHeldOnly?.Contains(h) == true)
                continue;
            if (placed.Add(key))
                targets.Add((h, true));
        }

        // 保有していない監視銘柄を、監視銘柄の順で後ろに。
        foreach (var w in watchlist)
        {
            if (!placed.Contains(Key(w)))
                targets.Add((w, false));
        }

        return targets;
    }

    private static (string, Market) Key(WatchedSymbol s) =>
        (s.Symbol.Trim().ToUpperInvariant(), s.Market);
}
