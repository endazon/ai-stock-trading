using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision;

// 🔴 FR-02, FR-04, UC-01, ADR-0051, #1286, IADR-0521 決定 1・2: 定時サイクルの判断対象（監視銘柄 ∪ 保有中の銘柄）。
//   - 監視銘柄はそのままの順で先に並べ、出口専用にしない（監視銘柄に在る保有銘柄は従来どおり新規建ても判断できる）。
//   - 監視銘柄に無い保有銘柄（保有のみ）だけを、保有の応答の順で末尾に足し、出口専用（ExitOnly）にする。
//   - 照合は監視銘柄の所属の判定（TradeDecisionPromptBuilder.WatchlistSection）と同じく、市場が一致し、銘柄は前後の空白を除いて
//     大文字小文字を区別しない。保有のみの銘柄の重複は 1 件にまとめる。
//   - held が null（不明）なら監視銘柄だけを返す（従来の巡回と同じ）。
//   - 🔴 IADR-0521 決定 4: excludedHeldOnly（全注文が審査で必ず拒否される保有のみの銘柄＝市場の無効・禁止銘柄）は足さない。
//     決済も必ず拒否されるため、判断しても LLM の費用と拒否・通知の繰り返しにしかならない。監視銘柄からは外さない（従来どおり）。
//   - 保有のみの銘柄は末尾に並ぶため、サイクルが実行時間の上限で打ち切られたときに最初に落ちる（IADR-0521 決定 3）。
public static class ScheduledJudgmentTargets
{
    public static IReadOnlyList<(WatchedSymbol Symbol, bool ExitOnly)> Build(
        IReadOnlyList<WatchedSymbol> watchlist,
        IReadOnlyList<WatchedSymbol>? held,
        IReadOnlySet<WatchedSymbol>? excludedHeldOnly = null)
    {
        ArgumentNullException.ThrowIfNull(watchlist);

        var targets = new List<(WatchedSymbol, bool)>(watchlist.Count + (held?.Count ?? 0));
        var seen = new HashSet<(string, Market)>();
        foreach (var w in watchlist)
        {
            targets.Add((w, false));
            seen.Add(Key(w));
        }

        if (held is null)
            return targets;

        foreach (var h in held)
        {
            if (excludedHeldOnly?.Contains(h) == true)
                continue;
            if (seen.Add(Key(h)))
                targets.Add((h, true));
        }

        return targets;
    }

    private static (string, Market) Key(WatchedSymbol s) =>
        (s.Symbol.Trim().ToUpperInvariant(), s.Market);
}
