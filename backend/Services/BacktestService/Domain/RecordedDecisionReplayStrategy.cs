using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace BacktestService.Domain;

// FR-04, FR-15, FR-20, ADR-0008, ADR-0033 決定2, #632, IADR-0318: **記録再生戦略**。
//
// ADR-0033 は Stage 0 の評価対象を「取引判断サービスの AI 判断そのもの」と定め、記録・再生方式を採った。
// 本型はその「再生」であり、**記録した判断列を注文へ写す純関数**である。
// 🔴 FR-10, #1209, IADR-0507: ただし再生の時点で**新規建て**になる注文は、本番と同じ 2 統制に当たれば写さない（見送る）——
// (1) 最小の名目額（記録器が本番と同じ関数で判定した `EntryBelowMinimumNotional`。理由 `SizedBelowMinimumNotional`）、
// (2) 判断由来の決済の後の同日・同方向（共有カーネルの `DecisionExitReentry`＝本番の審査と同じ述語。理由 `DecisionExitSameDay`）。
// 建玉は記録器が知らず再生にしか無いため、判定はここで行う。判定に要る建玉と決済は、その走行で当日までに渡されたバー
// （`BacktestContext.History`）から決定的に組み直す —— 同じ戦略を基準・コスト 2 倍・ウォークフォワードの各窓で使い回すので、
// 走行をまたぐ状態を持たない（同じ入力なら同じ出力）。
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
    private readonly Dictionary<DateOnly, List<RecordedOrder>> _ordersByDay;

    public RecordedDecisionReplayStrategy(Stage0DecisionRecordSet recordSet)
    {
        ArgumentNullException.ThrowIfNull(recordSet);

        From = recordSet.From;
        To = recordSet.To;
        StrategyId = recordSet.StrategyId;

        // 同一 (銘柄, 市場, AsOf) の重複は後勝ちで畳む（BacktestSimulator / MaterializedBarDataSource の重複規則と
        // 揃える）。畳む前に安定順へ並べ、記録の列挙順の揺れが再生結果へ漏れないようにする。
        var deduped = new Dictionary<(DateOnly AsOf, string Symbol, Market Market), Stage0DecisionRecord>();
        foreach (var record in recordSet.Records ?? [])
        {
            deduped[(record.AsOf, record.Symbol, record.Market)] = record;
        }

        var excluded = 0;
        var modelMismatch = 0;
        var excludedWithQuantity = 0;
        var evaluated = 0;
        var excludedKinds = new HashSet<Stage0AsOfInputKind>();

        _ordersByDay = [];
        foreach (var ((asOf, symbol, market), record) in deduped
            .OrderBy(e => e.Key.AsOf)
            .ThenBy(e => e.Key.Symbol, StringComparer.Ordinal)
            .ThenBy(e => e.Key.Market))
        {
            // 🔴 FR-15, ADR-0036 決定1, #749, IADR-0387: **再構成できなかった as-of 入力に依存する判断は
            // 判定母集団から外す。** 注文を写さないことで、その判断は成績（DSR・最大 DD・コスト 2 倍感度・
            // ウォークフォワード）のどこにも寄与しない ——「痩せた入力で動く別の判断器」を測った結果を
            // Stage 0 の合格根拠として引かない、というのが同決定の要求である。
            // ［2026-09-24 追記 / PR #931 監査］「どこにも寄与しない」は**見送りにしか成り立たない**。数量を持つ判断を
            // 外すと、差分で積み上がる再生では残した判断の経路が歪む（IADR-0387 決定3 追記。遮断は
            // `Stage0ReplayEvaluation` の `ExcludedDecisionAltersReplayPath`）。
            //
            // 🔴 **見送り（Hold）の記録も除外として数える。** 数量 0 の記録は注文を作らない点で除外後と
            // 同じ振る舞いになるが、**母集団から外れたという事実は数量と無関係**であり、混ぜると
            // 「AI が見送った」と「合否から外した」が件数の上で区別できなくなる。
            var kinds = Stage0AsOfInputs.NotReconstructableKinds(record.AsOfInputs);
            // 🔴 FR-15, ADR-0011, ADR-0054 決定3, #1196, IADR-0498: **両層のどちらかの実効モデルがピン（`LlmAssignments`）と違う判断は
            // 判定母集団から外す**（不明も一致と読まない）。別モデルが答えた判断で合格すれば、両層の組での合格（実弾解禁の必須ゲート）
            // にならない。一次を記録していない記録は対象外 —— 記録集合ごと評価不能として `Stage0ReplayEvaluation` が判定を組ませない。
            var mismatched = Stage0TwoTierModels.IsScreeningRecorded(record)
                && !Stage0TwoTierModels.MatchesPinnedAssignments(record);
            if (kinds.Count > 0 || mismatched)
            {
                excluded++;
                if (mismatched)
                    modelMismatch++;
                // IADR-0387 決定3［2026-09-24 追記 / PR #931 監査］: 数量を持つ判断を外すと、差分で積み上がる
                // 再生では残した判断の経路が歪む。ここでは数えるだけで、遮断は `Stage0ReplayEvaluation` が行う。
                if (record.SignedQuantity != 0)
                    excludedWithQuantity++;
                foreach (var kind in kinds)
                    excludedKinds.Add(kind);
                continue;
            }

            evaluated++;

            // 見送り（Hold）は数量 0 であり、注文を作らない（無発注と「0 株の注文」を区別しない）。
            var quantity = record.SignedQuantity;
            if (quantity == 0)
                continue;

            if (!_ordersByDay.TryGetValue(asOf, out var orders))
            {
                orders = [];
                _ordersByDay[asOf] = orders;
            }

            orders.Add(new RecordedOrder(new BacktestOrder(symbol, market, quantity), record.EntryBelowMinimumNotional == true));
        }

        ExcludedDecisionCount = excluded;
        ModelMismatchDecisionCount = modelMismatch;
        ExcludedDecisionWithQuantityCount = excludedWithQuantity;
        EvaluatedDecisionCount = evaluated;
        ExcludedInputKinds = [.. Stage0AsOfInputs.DeclarableKinds.Where(excludedKinds.Contains)];
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
    /// FR-15, ADR-0036 決定1, #749, IADR-0387: 再構成できなかった as-of 入力に依存するため
    /// **判定母集団から外した**判断の件数（重複を畳んだ後の数）。
    /// </summary>
    public int ExcludedDecisionCount { get; }

    /// <summary>
    /// FR-15, ADR-0036 決定1, #749, IADR-0387 決定3［2026-09-24 追記 / PR #931 監査］: 外した判断のうち
    /// **数量を持つ（見送りでない）**ものの件数（重複を畳んだ後の数）。
    /// <para>
    /// 🔴 **1 以上なら、残した判断の再生経路は AI が実際に取った経路ではない。** 注文は差分であり
    /// `SignedInventory` で積み上がるため、入口を外せば残した出口が裸の空売りを建て、出口を外せば建玉が
    /// 開いたまま残る。見送り（数量 0）は注文を作らないため、外しても経路は変わらない。
    /// </para>
    /// </summary>
    public int ExcludedDecisionWithQuantityCount { get; }

    /// <summary>
    /// FR-15, ADR-0054 決定3, #1196, IADR-0498: 一次か本判断の実効モデルがピンと違った（不明を含む）ため判定母集団から外した
    /// 判断の件数（<see cref="ExcludedDecisionCount"/> の内数・重複を畳んだ後の数）。
    /// </summary>
    public int ModelMismatchDecisionCount { get; }

    /// <summary>判定母集団に残った判断の件数（重複を畳んだ後の数）。**0 なら評価対象が成立していない。**</summary>
    public int EvaluatedDecisionCount { get; }

    /// <summary>外す理由になった入力の種別（安定順・除外が無ければ空）。</summary>
    public IReadOnlyList<Stage0AsOfInputKind> ExcludedInputKinds { get; }

    /// <summary>
    /// 当日（<c>context.AsOf</c>）の記録を引き、目標注文へ写す。
    /// <para>
    /// 🔴 **記録集合の期間外では 1 件も発注しない。** ウォークフォワードの窓や感度分析で、記録の無い期間の
    /// バーが渡ることがある。そこで発注すれば「記録していない判断」が成績に混ざり、評価対象が
    /// AI 判断そのものでなくなる（期間外に記録が無いのは自明に見えるが、**期間の検査を明示的に置く**のは
    /// 記録の取り違えで別期間の判断が紛れ込む経路を断つためである）。
    /// </para>
    /// <para>記録の無い日も無発注である（判断していない日に注文を発明しない）。</para>
    /// <para>FR-10, #1209, IADR-0507: 新規建てになる注文のうち本番の 2 統制に当たるものは写さない（<see cref="Replay"/>）。</para>
    /// </summary>
    public IReadOnlyList<BacktestOrder> DecideOrders(BacktestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.AsOf < From || context.AsOf > To || !_ordersByDay.ContainsKey(context.AsOf))
            return [];

        return Replay(context.History, context.AsOf).Orders;
    }

    /// <summary>
    /// FR-10, FR-15, #1209, IADR-0507: その走行で当日（<paramref name="asOf"/>）までに渡されたバー（<paramref name="history"/>）から、
    /// 建玉と判断由来の決済を判断日ごとに組み直し、当日に写す注文と、当日までに見送った新規建てを返す（純関数）。
    /// <para>
    /// 組み直しは <see cref="BacktestSimulator"/> と同じ規則に従う —— 建玉ゼロから始め、判断日の注文は<b>次の取引日</b>（バーのある日）の始値で、
    /// その銘柄のバーがあるときだけ約定する。したがって当日の建玉はシミュレータの建玉と一致する。
    /// </para>
    /// </summary>
    public Stage0ReplayDay Replay(IReadOnlyList<PriceBar> history, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(history);

        var barsByDay = history
            .Where(b => b.Date <= asOf)
            .GroupBy(b => b.Date)
            .ToDictionary(g => g.Key, g => g.Select(b => (b.Symbol, b.Market)).ToHashSet());
        // 当日は必ず判断日に含める（シミュレータは当日のバーを足してから呼ぶが、バーの無い文脈でも当日の記録は引く＝建玉ゼロとして扱う）。
        if (!barsByDay.ContainsKey(asOf))
            barsByDay[asOf] = [];
        var days = barsByDay.OrderBy(e => e.Key).Select(e => (Day: e.Key, Bars: e.Value));

        var inventory = new Dictionary<(string Symbol, Market Market), int>();
        var exits = new Dictionary<(string Symbol, Market Market), List<ReplayExit>>();
        var skipped = new List<Stage0ReplaySkippedEntry>();
        IReadOnlyList<(BacktestOrder Order, ReplayExit? Exit)> pending = [];
        IReadOnlyList<BacktestOrder> todays = [];

        foreach (var (day, bars) in days)
        {
            // 1) 前の取引日に決めた注文を当日の始値で約定させる（バーの無い銘柄は約定しない。シミュレータと同じ）。
            foreach (var (order, exit) in pending)
            {
                var key = (order.Symbol, order.Market);
                if (!bars.Contains(key))
                    continue;

                inventory[key] = inventory.GetValueOrDefault(key) + order.SignedQuantity;
                exit?.FilledOn.Add(day);
            }

            // 2) 当日の記録を、当日の建玉に照らして注文へ写す（期間外・記録の無い日は無発注）。
            var decided = new List<(BacktestOrder Order, ReplayExit? Exit)>();
            if (day >= From && day <= To && _ordersByDay.TryGetValue(day, out var recorded))
            {
                foreach (var (order, belowMinimumNotional) in recorded)
                {
                    var key = (order.Symbol, order.Market);
                    var held = inventory.GetValueOrDefault(key);

                    // 建玉を減らす（符号が逆の）注文は判断由来の決済。建玉を跨ぐ注文も決済として扱う（新規建ての判定を掛けない）。
                    if (held != 0 && Math.Sign(held) != Math.Sign(order.SignedQuantity))
                    {
                        var exit = new ReplayExit(order.SignedQuantity > 0 ? TradeSide.Buy : TradeSide.Sell, day);
                        if (!exits.TryGetValue(key, out var list))
                        {
                            list = [];
                            exits[key] = list;
                        }

                        list.Add(exit);
                        decided.Add((order, exit));
                        continue;
                    }

                    var reason = EntryControl(order, belowMinimumNotional, exits.GetValueOrDefault(key), day);
                    if (reason is { } r)
                    {
                        skipped.Add(new Stage0ReplaySkippedEntry(
                            day, order.Symbol, order.Market, order.SignedQuantity, r.Skip, r.Rejection));
                        continue;
                    }

                    decided.Add((order, null));
                }
            }

            pending = decided;
            todays = [.. decided.Select(d => d.Order)];
        }

        return new Stage0ReplayDay(todays, skipped);
    }

    // FR-10, #1209, IADR-0507: 新規建て（建玉 0、または建玉と同じ符号）に本番の 2 統制を当てる。本番と同じ順で評価する ——
    // 判断由来の決済の後の同日・同方向は新規建ての可否の口が LLM の前に止める（名目額の判定まで届かない）。
    private static (DecisionSkipReason? Skip, RejectionReason? Rejection)? EntryControl(
        BacktestOrder order, bool belowMinimumNotional, List<ReplayExit>? exits, DateOnly day)
    {
        var entrySide = order.SignedQuantity > 0 ? TradeSide.Buy : TradeSide.Sell;
        if (exits is not null)
        {
            // 承認の取引日＝判断日、約定の取引日＝約定したバーの日（本番の射影が時刻から写す値と同じ意味）。
            var sides = DecisionExitReentry.Project(
                exits.Select(e => new DecisionExitOnTradingDays(e.CloseSide, e.ApprovedOn, e.FilledOn)), day);
            if (DecisionExitReentry.BlocksEntry(sides.LongSide, sides.ShortSide, entrySide))
                return (null, RejectionReason.DecisionExitSameDay);
        }

        if (belowMinimumNotional)
            return (DecisionSkipReason.SizedBelowMinimumNotional, null);

        return null;
    }

    private readonly record struct RecordedOrder(BacktestOrder Order, bool BelowMinimumNotional);

    // 再生の中の 1 つの判断由来の決済（約定日は約定したときに足す）。
    private sealed record ReplayExit(TradeSide CloseSide, DateOnly ApprovedOn)
    {
        public List<DateOnly> FilledOn { get; } = [];
    }
}

/// <summary>
/// FR-10, FR-15, #1209, IADR-0507: 再生の 1 日分の結果。<see cref="Orders"/> は当日に写す注文、<see cref="SkippedEntries"/> は
/// その走行で当日までに本番の統制に当たって見送った新規建て（判断日順）。
/// </summary>
public sealed record Stage0ReplayDay(
    IReadOnlyList<BacktestOrder> Orders,
    IReadOnlyList<Stage0ReplaySkippedEntry> SkippedEntries);

/// <summary>
/// FR-10, FR-15, #1209, IADR-0507: 再生で見送った新規建て。理由は本番の列挙そのもの —— 最小の名目額は判断の見送り
/// （<see cref="DecisionSkipReason.SizedBelowMinimumNotional"/>）、判断由来の決済の後の同日・同方向は審査の拒否
/// （<see cref="RejectionReason.DecisionExitSameDay"/>）。どちらか一方だけが入る。
/// </summary>
public sealed record Stage0ReplaySkippedEntry(
    DateOnly AsOf,
    string Symbol,
    Market Market,
    int SignedQuantity,
    DecisionSkipReason? SkipReason,
    RejectionReason? RejectionReason);
