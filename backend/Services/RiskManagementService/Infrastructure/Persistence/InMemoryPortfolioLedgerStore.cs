using System.Collections.Concurrent;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-10, FR-05, IADR-0018: 取引台帳のインメモリ実装（テスト・単体実行用）。PostgreSQL 永続化は Worker の
// EfPortfolioLedgerStore で差し替える。承認は DecisionId、約定は OrderId で冪等に保持する。
public sealed class InMemoryPortfolioLedgerStore : IPortfolioLedgerStore
{
    private readonly ConcurrentDictionary<Guid, ApprovalRecord> _approvals = new();
    private readonly ConcurrentDictionary<string, FillRecord> _fills = new();

    // #849, IADR-0350 決定 2: 利用者が承認した乖離の取り込み（冪等キー → 行）。
    private readonly ConcurrentDictionary<string, LedgerDriftAdoption> _adoptions = new();

    public void AppendApproval(
        Guid decisionId,
        OrderIntent intent,
        DateTimeOffset approvedAt,
        decimal? fxRateBaseToDisplay = null)
    {
        ArgumentNullException.ThrowIfNull(intent);
        // #611, IADR-0286 決定1: 認識時レート（1 USD あたりの円）を承認時点で固定する（EfPortfolioLedgerStore と同一の意味論）。
        _approvals.TryAdd(decisionId, new ApprovalRecord(intent, approvedAt, fxRateBaseToDisplay));
    }

    public bool AppendFill(
        Guid decisionId,
        string orderId,
        int filledQuantity,
        decimal averagePrice,
        DateTimeOffset executedAt,
        BrokerProvider? provider = null)
    {
        if (!_approvals.ContainsKey(decisionId))
            return false;

        // #270, IADR-0113: 単調 upsert（EfPortfolioLedgerStore と同一の意味論）。約定数量は累積値であり、
        // 累積が増えたときだけ更新する。再送・順序前後で二重計上も巻き戻りも起こさない。
        // #569, IADR-0271: 発注先は**分かったときだけ上書きする**（EfPortfolioLedgerStore と同一の意味論）。
        _fills.AddOrUpdate(
            orderId,
            _ => new FillRecord(decisionId, filledQuantity, averagePrice, executedAt, provider),
            (_, current) => filledQuantity > current.FilledQuantity
                ? new FillRecord(decisionId, filledQuantity, averagePrice, executedAt, provider ?? current.Provider)
                : current);
        return true;
    }

    // FR-20, #386, IADR-0149 決定2: 承認済み注文の建玉効果を DecisionId で引く（未承認は null＝不明）。
    public PositionEffect? FindApprovedPositionEffect(Guid decisionId) =>
        _approvals.TryGetValue(decisionId, out var approval) ? approval.Intent.PositionEffect : null;

    // FR-19, #425, IADR-0165: 承認 Intent を DecisionId で引く（未承認は null＝不明）。
    public OrderIntent? FindApprovedIntent(Guid decisionId) =>
        _approvals.TryGetValue(decisionId, out var approval) ? approval.Intent : null;

    public IReadOnlyList<LedgerFill> GetFills()
    {
        var result = new List<LedgerFill>();
        foreach (var fill in _fills.Values)
        {
            if (!_approvals.TryGetValue(fill.DecisionId, out var approval))
                continue;

            var intent = approval.Intent;
            // IADR-0107: 承認 Intent の換算レートを台帳の約定に引き継ぐ（金額集計は基準通貨で行う）。
            result.Add(new LedgerFill(
                intent.Symbol, intent.Market, intent.Side, intent.PositionEffect,
                fill.FilledQuantity, fill.AveragePrice, fill.ExecutedAt, intent.StopLossPrice, intent.FxRateToBase,
                // #563, IADR-0269: 判断記録（監査台帳の TradeDecisionMade）と突き合わせる相関キー。
                fill.DecisionId,
                // #569, IADR-0271: **実際に発注したアダプタの発注先**（不明は null）。intent.Mode へ倒さない。
                fill.Provider,
                // #611, IADR-0286 決定1: 認識時レート（1 USD あたりの円）。未記録は null のまま（既定へ倒さない）。
                approval.FxRateBaseToDisplay));
        }

        // #849, IADR-0350 決定 2: 乖離の取り込み行を合流させる（EfPortfolioLedgerStore と同一の意味論）。
        foreach (var a in _adoptions.Values)
        {
            result.Add(new LedgerFill(
                a.Symbol, a.Market, a.Side, PositionEffect.Close, a.Quantity, a.CostBasisPrice, a.AdoptedAt,
                StopLossPrice: null, FxRateToBase: a.FxRateToBase, Origin: TradeOrigin.ManualAdoption));
        }

        return result;
    }

    // FR-10, UC-06, #848, IADR-0117: 承認が終端になったことを記録する（EfPortfolioLedgerStore と同一の意味論）。
    public void MarkTerminal(Guid decisionId, OrderStatus terminalStatus, DateTimeOffset terminalAt)
    {
        // 終端を捏造しない（Accepted / PartiallyFilled は「まだ動く」）。
        // #848 改定 2: 門は AbandonsUnfilledRemainder（取消・失効・拒否）であって IsTerminal ではない。
        // **全量約定（Filled）は書かない**（EfPortfolioLedgerStore と同一の意味論）。
        if (!OrderStatusLifecycle.AbandonsUnfilledRemainder(terminalStatus))
            return;

        // 相関する承認が無ければ**書かない**（AddOrUpdate は無い鍵を作ってしまうので使わない）。
        // 単調・冪等: 既に終端なら動かさない（最初の終端が真）。
        while (_approvals.TryGetValue(decisionId, out var current))
        {
            if (current.TerminalAt is not null)
                return;

            var updated = current with { TerminalAt = terminalAt, TerminalStatus = terminalStatus };
            if (_approvals.TryUpdate(decisionId, updated, current))
                return;
        }
    }

    // FR-05, FR-10, UC-06, #852, IADR-0356: 見送り（発注していない）を記録する
    //（EfPortfolioLedgerStore と同一の意味論）。門（理由が「確実に未発注」か）は呼び出し側が持つ。
    public void MarkForgone(Guid decisionId, DateTimeOffset forgoneAt)
    {
        // 相関する承認が無ければ**書かない**。単調・冪等: 既に終端（見送りを含む）なら動かさない。
        while (_approvals.TryGetValue(decisionId, out var current))
        {
            if (current.TerminalAt is not null)
                return;

            // 🔴 TerminalStatus は **null のまま**（見送りは注文状態を持たない。IADR-0211）。
            var updated = current with { TerminalAt = forgoneAt, TerminalStatus = null };
            if (_approvals.TryUpdate(decisionId, updated, current))
                return;
        }
    }

    // #849, IADR-0350 決定 2: 追記専用・冪等キーで 1 件に絞る（EfPortfolioLedgerStore と同一の意味論）。
    public bool AppendDriftAdoption(LedgerDriftAdoption adoption)
    {
        ArgumentNullException.ThrowIfNull(adoption);
        return _adoptions.TryAdd(adoption.IdempotencyKey, adoption);
    }

    // #870, IADR-0360 決定 2: 取り込みそのものを読む口（EfPortfolioLedgerStore と同一の意味論）。
    public IReadOnlyList<LedgerDriftAdoption> GetDriftAdoptions() =>
        [.. _adoptions.Values.OrderBy(a => a.AdoptedAt).ThenBy(a => a.Id)];

    // #292, IADR-0117: 処理中の決済数量（EfPortfolioLedgerStore と同一の意味論）。
    public int GetInFlightCloseQuantity(string symbol, Market market, DateTimeOffset approvedAtOrAfter)
    {
        // DecisionId ごとの約定累計。1 承認に複数の注文行が対応し得る形（リコンサイル経路）でも取りこぼさない。
        var filledByDecision = new Dictionary<Guid, int>();
        foreach (var fill in _fills.Values)
        {
            filledByDecision.TryGetValue(fill.DecisionId, out var current);
            filledByDecision[fill.DecisionId] = current + fill.FilledQuantity;
        }

        var total = 0;
        foreach (var (decisionId, approval) in _approvals)
        {
            var intent = approval.Intent;
            if (intent.PositionEffect != PositionEffect.Close
                || intent.Symbol != symbol
                || intent.Market != market
                || approval.ApprovedAt < approvedAtOrAfter
                // #848: 終端になったと**確認できた**承認は数えない（残りは二度と約定しない）。
                // null＝未確認は従来どおり処理中として数える（fail-safe。除外し過ぎるとショート化する）。
                || approval.TerminalAt is not null)
            {
                continue;
            }

            // 未約定 = 承認数量 − 約定累計。約定が承認を超えた場合（部分列挙・訂正）も負に振れさせない。
            total += Math.Max(0, intent.Quantity - filledByDecision.GetValueOrDefault(decisionId));
        }

        return total;
    }

    // #848, #852, IADR-0117 / IADR-0356: 終端（見送りを含む）の記録を読む**読み取り専用**の口。
    // 台帳の意味論（単調・見送りは TerminalStatus を立てない）は数量の集計だけでは確かめられないため、
    // EF 実装が `db.ApprovedOrders.Find(id)` の行を直接読むのと同じものを、インメモリ実装でも読めるようにする。
    // 相関する承認が無ければ (null, null)。**書き込みはしない**（判定・集計はこの口を通さない）。
    public (DateTimeOffset? TerminalAt, OrderStatus? TerminalStatus) TerminalStateOf(Guid decisionId) =>
        _approvals.TryGetValue(decisionId, out var approval)
            ? (approval.TerminalAt, approval.TerminalStatus)
            : (null, null);

    // FR-10, #829, IADR-0346 決定1: 承認の一覧（InMemoryWorkingEntryOrderSource が未終端の新規建てを切り出す）。
    internal IReadOnlyList<(Guid DecisionId, OrderIntent Intent, DateTimeOffset ApprovedAt)> SnapshotApprovals() =>
        _approvals.Select(a => (a.Key, a.Value.Intent, a.Value.ApprovedAt)).ToList();

    private sealed record ApprovalRecord(OrderIntent Intent, DateTimeOffset ApprovedAt, decimal? FxRateBaseToDisplay = null)
    {
        // #848, IADR-0117: 終端になったと確認できた時刻と状態（ApprovedOrderRow と同じ意味論）。
        // null＝未確認。判定に使うのは TerminalAt だけで、TerminalStatus は診断用である。
        // #852, IADR-0356: 見送りは TerminalAt だけを立て、TerminalStatus は null のままにする
        //（見送りは注文状態を持たない。IADR-0211）。
        public DateTimeOffset? TerminalAt { get; init; }

        public OrderStatus? TerminalStatus { get; init; }
    }

    private sealed record FillRecord(
        Guid DecisionId,
        int FilledQuantity,
        decimal AveragePrice,
        DateTimeOffset ExecutedAt,
        BrokerProvider? Provider);
}
