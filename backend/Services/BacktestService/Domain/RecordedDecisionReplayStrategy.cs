using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Trading;

namespace BacktestService.Domain;

// FR-04, FR-15, FR-20, ADR-0008, ADR-0033 決定2, #632, IADR-0318: **記録再生戦略**。
//
// ADR-0033 は Stage 0 の評価対象を「取引判断サービスの AI 判断そのもの」と定め、記録・再生方式を採った。
// 本型はその「再生」であり、**記録した判断列をそのまま注文へ写すだけの純関数**である。
//
// 🔴 **LLM をここから呼ばない。** IADR-0043 は `IBacktestStrategy` を「I/O・時刻・乱数・外部 API に依存しない
// 純関数」と定義した。ADR-0033 決定2 はその契約を**覆さない**と明記しており、非決定性は記録の側へ閉じ込める。
// 同じ記録集合を与えれば、ウォークフォワードでもコスト 2 倍感度でも DSR/PBO でも同じ判断列が再生される。
//
// 🔴 **サイジングを再計算しない。** 数量は記録が持つ `SignedQuantity` をそのまま使う。ここで再計算すると
// 「記録した AI 判断」ではなく「記録した AI 判断＋いまのサイジング規則」を評価することになり、
// 評価対象が本番と一致しなくなる（ADR-0011 が段階ゲートの前提とした一致が崩れる）。
public sealed class RecordedDecisionReplayStrategy : IBacktestStrategy
{
    private readonly Dictionary<DateOnly, List<BacktestOrder>> _ordersByDay;

    public RecordedDecisionReplayStrategy(Stage0DecisionRecordSet recordSet)
    {
        ArgumentNullException.ThrowIfNull(recordSet);

        From = recordSet.From;
        To = recordSet.To;
        StrategyId = recordSet.StrategyId;

        // 同一 (銘柄, 市場, AsOf) の重複は後勝ちで畳む（BacktestSimulator / MaterializedBarDataSource の重複規則と
        // 揃える）。畳む前に安定順へ並べ、記録の列挙順の揺れが再生結果へ漏れないようにする。
        var deduped = new Dictionary<(DateOnly AsOf, string Symbol, Market Market), int>();
        foreach (var record in recordSet.Records ?? [])
        {
            deduped[(record.AsOf, record.Symbol, record.Market)] = record.SignedQuantity;
        }

        _ordersByDay = [];
        foreach (var ((asOf, symbol, market), quantity) in deduped
            .OrderBy(e => e.Key.AsOf)
            .ThenBy(e => e.Key.Symbol, StringComparer.Ordinal)
            .ThenBy(e => e.Key.Market))
        {
            // 見送り（Hold）は数量 0 であり、注文を作らない（無発注と「0 株の注文」を区別しない）。
            if (quantity == 0)
                continue;

            if (!_ordersByDay.TryGetValue(asOf, out var orders))
            {
                orders = [];
                _ordersByDay[asOf] = orders;
            }

            orders.Add(new BacktestOrder(symbol, market, quantity));
        }
    }

    /// <summary>記録集合が覆う期間の始端（両端含む）。</summary>
    public DateOnly From { get; }

    /// <summary>記録集合が覆う期間の終端（両端含む）。</summary>
    public DateOnly To { get; }

    /// <summary>
    /// 戦略の同一性（`BacktestEvaluated.StrategyId`）。記録の内容から導出された値をそのまま名乗る
    /// （IADR-0281 決定3 の「戦略の変更」を機械判定する鍵）。
    /// </summary>
    public string StrategyId { get; }

    /// <summary>
    /// 当日（<c>context.AsOf</c>）の記録を引き、目標注文へ写す。
    /// <para>
    /// 🔴 **記録集合の期間外では 1 件も発注しない。** ウォークフォワードの窓や感度分析で、記録の無い期間の
    /// バーが渡ることがある。そこで発注すれば「記録していない判断」が成績に混ざり、評価対象が
    /// AI 判断そのものでなくなる（期間外に記録が無いのは自明に見えるが、**期間の検査を明示的に置く**のは
    /// 記録の取り違えで別期間の判断が紛れ込む経路を断つためである）。
    /// </para>
    /// <para>記録の無い日も無発注である（判断していない日に注文を発明しない）。</para>
    /// </summary>
    public IReadOnlyList<BacktestOrder> DecideOrders(BacktestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.AsOf < From || context.AsOf > To)
            return [];

        return _ordersByDay.TryGetValue(context.AsOf, out var orders) ? orders : [];
    }
}
