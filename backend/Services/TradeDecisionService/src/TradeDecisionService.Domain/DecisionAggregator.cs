namespace AiStockTrading.TradeDecision.Domain;

// FR-04, FR-11, ADR-0003, IADR-0039: 多数決の集約（純関数）。同一入力を複数回 LLM 実行した各判断を安定化する。
// 最多得票の action を採り、同数タイ・空入力は安全側 Hold（ADR-0003「不確実なら取引しない」）。
// 数値は勝利票から合成せず、参照価格の下側中央値を持つ「代表票」を一体で採用し、実在する 1 票の根拠（FR-11）を保つ。
public static class DecisionAggregator
{
    public static DecisionVoteResult Aggregate(IReadOnlyList<LlmDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);

        var total = decisions.Count;
        if (total == 0)
        {
            return new DecisionVoteResult(LlmDecision.Hold, 0, 0);
        }

        // action ごとの得票を数え、最多得票の action を求める。首位が同数で並ぶ（タイ）なら Hold に倒す。
        var tally = new Dictionary<TradeAction, int>();
        foreach (var d in decisions)
        {
            tally[d.Action] = tally.GetValueOrDefault(d.Action) + 1;
        }

        var topCount = tally.Values.Max();
        var leaders = tally.Where(kv => kv.Value == topCount).Select(kv => kv.Key).ToList();
        if (leaders.Count != 1)
        {
            // 首位タイ（不確実）→ 安全側 Hold。得票数は監査のため総数のみ残す（合意 0）。
            return new DecisionVoteResult(LlmDecision.Hold, total, 0);
        }

        var winner = leaders[0];
        var agreement = topCount;
        if (winner == TradeAction.Hold)
        {
            return new DecisionVoteResult(LlmDecision.Hold, total, agreement);
        }

        // Buy/Sell 勝利: 勝利票のうち参照価格の下側中央値を持つ代表票を一体で採る（合成しない）。
        // 決定的順序（参照価格→損切り幅→根拠）でソートし、下側中央 index=(n-1)/2 を選ぶ。
        var winningVotes = decisions
            .Where(d => d.Action == winner)
            .OrderBy(d => d.ReferencePrice)
            .ThenBy(d => d.StopLossDistancePerShare)
            .ThenBy(d => d.Rationale, StringComparer.Ordinal)
            .ToList();
        var representative = winningVotes[(winningVotes.Count - 1) / 2];

        return new DecisionVoteResult(representative, total, agreement);
    }
}

// IADR-0039: 多数決の集約結果。Decision は下流サイジングへ、TotalVotes/AgreementVotes は監査（FR-11）へ。
// AgreementVotes は勝利 action の得票数（タイ・空は 0）。
public sealed record DecisionVoteResult(LlmDecision Decision, int TotalVotes, int AgreementVotes);
