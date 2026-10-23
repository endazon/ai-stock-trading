using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Infrastructure.Persistence;

// FR-05: 発注結果ストアのインメモリ実装。PostgreSQL 永続化は Slice B で差し替える。
public sealed class InMemoryExecutedOrderStore : IExecutedOrderStore
{
    private readonly Lock _gate = new();
    private readonly List<ExecutionRecord> _records = [];

    // 🔴 #1048, IADR-0481 決定3: 打ち切りを記録した追跡の起点（OrderId → 起点）。EF の行の列に当たる。
    private readonly Dictionary<string, DateTimeOffset> _abandonedFrom = new(StringComparer.Ordinal);

    // 🔴 #1442, IADR-0573 決定2: 発行待ちの印（OrderId の集合）。EF の行の列 OrderExecutedPublishPending に当たる。
    private readonly HashSet<string> _publishPending = new(StringComparer.Ordinal);

    public void Save(ExecutionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            _records.Add(record);
        }
    }

    public IReadOnlyList<ExecutionRecord> GetAll()
    {
        lock (_gate)
        {
            return _records.AsEnumerable().Reverse().ToList();
        }
    }

    public ExecutionRecord? FindByDecisionId(Guid decisionId)
    {
        lock (_gate)
        {
            return _records.FirstOrDefault(r => r.DecisionId == decisionId);
        }
    }

    // #270, IADR-0113: 追跡対象＝非終端かつ追跡上限内の記録を古い順に返す。
    public IReadOnlyList<ExecutionRecord> FindPendingSince(DateTimeOffset since, int batchSize)
    {
        lock (_gate)
        {
            return _records
                .Where(r => OrderStatusLifecycle.IsPending(r.Status) && r.ExecutedAt >= since)
                .OrderBy(r => r.ExecutedAt)
                .Take(batchSize)
                .ToList();
        }
    }

    // FR-10, #958, IADR-0406 決定1: 指定した注文 ID の非終端の記録を古い順に返す（追跡上限は見ない）。
    public IReadOnlyList<ExecutionRecord> FindPendingByOrderIds(IReadOnlyCollection<string> orderIds)
    {
        ArgumentNullException.ThrowIfNull(orderIds);
        if (orderIds.Count == 0)
            return [];

        lock (_gate)
        {
            return _records
                .Where(r => OrderStatusLifecycle.IsPending(r.Status) && orderIds.Contains(r.OrderId))
                .OrderBy(r => r.ExecutedAt)
                .ToList();
        }
    }

    // FR-10, #1105, IADR-0461 決定1: 非終端の決済の記録を銘柄・市場・決済の方向で絞って古い順に返す（追跡上限は見ない）。
    public IReadOnlyList<ExecutionRecord> FindPendingCloses(string symbol, Market market, TradeSide closeSide)
    {
        ArgumentException.ThrowIfNullOrEmpty(symbol);

        lock (_gate)
        {
            return _records
                .Where(r => r.PositionEffect == PositionEffect.Close && OrderStatusLifecycle.IsPending(r.Status)
                    && r.Symbol == symbol && r.Market == market && r.Side == closeSide)
                .OrderBy(r => r.ExecutedAt)
                .ToList();
        }
    }

    // FR-10, #1121, IADR-0466 決定2: 新規建ての記録を銘柄・市場・エントリーの方向で絞って新しい順に返す（状態を問わない・上限つき）。
    public IReadOnlyList<ExecutionRecord> FindRecentOpens(string symbol, Market market, TradeSide entrySide, int limit)
    {
        ArgumentException.ThrowIfNullOrEmpty(symbol);

        lock (_gate)
        {
            return _records
                .Where(r => r.PositionEffect == PositionEffect.Open
                    && r.Symbol == symbol && r.Market == market && r.Side == entrySide)
                .OrderByDescending(r => r.ExecutedAt)
                .Take(limit)
                .ToList();
        }
    }

    // 🔴 FR-10, #1330, IADR-0534 決定2: 承認経路の決済のうち、まだ保護の縮小へ帰属させていない約定を持つ記録（古い順・上限つき）。
    public IReadOnlyList<ExecutionRecord> FindApprovedClosesWithUnreleasedFills(
        string symbol, Market market, TradeSide closeSide, int limit)
    {
        ArgumentException.ThrowIfNullOrEmpty(symbol);

        lock (_gate)
        {
            return _records
                .Where(r => r.PositionEffect == PositionEffect.Close && r.ApprovalOrigin is not null
                    && r.FilledQuantity > r.ProtectionReleasedQuantity
                    && r.Symbol == symbol && r.Market == market && r.Side == closeSide)
                .OrderBy(r => r.ExecutedAt)
                .Take(limit)
                .ToList();
        }
    }

    // 🔴 FR-10, #1330, IADR-0534 決定2: 帰属させた株数を進める（その値だけを書く。単調・約定数量以下）。
    public bool MarkProtectionReleased(string orderId, int releasedQuantity)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);

        lock (_gate)
        {
            var index = _records.FindIndex(r => r.OrderId == orderId);
            if (index < 0)
                return false;

            var record = _records[index];
            var next = Math.Min(releasedQuantity, record.FilledQuantity);
            if (next <= record.ProtectionReleasedQuantity)
                return false;

            _records[index] = record with { ProtectionReleasedQuantity = next };
            return true;
        }
    }

    // FR-10, #958, IADR-0406 決定3: 非終端の記録の追跡の起点を進める（時刻だけを書く）。
    public bool RenewTracking(string orderId, DateTimeOffset trackedFrom)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);

        lock (_gate)
        {
            var index = _records.FindIndex(r => r.OrderId == orderId);
            if (index < 0
                || !OrderStatusLifecycle.IsPending(_records[index].Status)
                || _records[index].ExecutedAt >= trackedFrom)
            {
                return false;
            }

            _records[index] = _records[index] with { ExecutedAt = trackedFrom };
            return true;
        }
    }

    // 🔴 FR-10, FR-11, #1048, IADR-0481 決定3: 追跡上限を過ぎた非終端の記録のうち、その起点で打ち切りを記録していないもの（古い順）。
    // 除外（Active な S0 の逆指値レグ）は件数の上限の前に行う（#1048 独立監査）。
    public IReadOnlyList<ExecutionRecord> FindTrackingExpired(
        DateTimeOffset before, int batchSize, IReadOnlyCollection<string>? excludedOrderIds = null)
    {
        var excluded = excludedOrderIds is null ? [] : new HashSet<string>(excludedOrderIds, StringComparer.Ordinal);
        lock (_gate)
        {
            return _records
                .Where(r => OrderStatusLifecycle.IsPending(r.Status) && r.ExecutedAt < before
                    && !(_abandonedFrom.TryGetValue(r.OrderId, out var from) && from == r.ExecutedAt)
                    && !excluded.Contains(r.OrderId))
                .OrderBy(r => r.ExecutedAt)
                .Take(batchSize)
                .ToList();
        }
    }

    // 🔴 FR-10, FR-11, #1048, IADR-0481 決定3: 打ち切りの印を書く（起点が進んでいれば書かない）。
    public bool MarkTrackingAbandoned(string orderId, DateTimeOffset trackedFrom)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);

        lock (_gate)
        {
            var record = _records.FirstOrDefault(r => r.OrderId == orderId);
            if (record is null || record.ExecutedAt != trackedFrom)
                return false;

            _abandonedFrom[orderId] = trackedFrom;
            return true;
        }
    }

    // 🔴 FR-10, FR-05, #1339, IADR-0536 決定2: 追跡を打ち切った（今の起点で印がある）非終端の承認経路の決済（古い順・上限つき）。
    public IReadOnlyList<ExecutionRecord> FindAbandonedApprovedCloses(int batchSize)
    {
        lock (_gate)
        {
            return _records
                .Where(r => r.PositionEffect == PositionEffect.Close && r.ApprovalOrigin is not null
                    && OrderStatusLifecycle.IsPending(r.Status)
                    && _abandonedFrom.TryGetValue(r.OrderId, out var from) && from == r.ExecutedAt)
                .OrderBy(r => r.ExecutedAt)
                .Take(batchSize)
                .ToList();
        }
    }

    // FR-10, FR-11, #1339, IADR-0536 決定4: 発注から一定時間たった非終端の承認経路の決済（古い順・上限つき）。
    public IReadOnlyList<ExecutionRecord> FindStaleApprovedCloses(DateTimeOffset placedBefore, int limit)
    {
        lock (_gate)
        {
            return _records
                .Where(r => r.PositionEffect == PositionEffect.Close && r.ApprovalOrigin is not null
                    && OrderStatusLifecycle.IsPending(r.Status) && r.ExecutedAt < placedBefore)
                .OrderBy(r => r.ExecutedAt)
                .Take(limit)
                .ToList();
        }
    }

    // 🔴 FR-10, ADR-0067 決定1・4, #1339, IADR-0539: 発注から N 分以上たって非終端の判断由来の決済で、系がまだ取消を求めていないもの（古い順・上限つき）。
    public IReadOnlyList<ExecutionRecord> FindUnfilledDecisionCloses(
        DateTimeOffset placedAtOrBefore, int limit, IReadOnlyCollection<string>? excludedOrderIds = null,
        DateTimeOffset? placedOnOrAfter = null)
    {
        var from = placedOnOrAfter ?? DateTimeOffset.MinValue;
        var excluded = excludedOrderIds is null ? [] : new HashSet<string>(excludedOrderIds, StringComparer.Ordinal);
        lock (_gate)
        {
            return _records
                .Where(r => r.PositionEffect == PositionEffect.Close && r.ApprovalOrigin == OrderApprovalOrigin.TradeDecision
                    && OrderStatusLifecycle.IsPending(r.Status) && r.UnfilledCancelRequestedAt is null
                    && r.ExecutedAt <= placedAtOrBefore && r.ExecutedAt >= from && !excluded.Contains(r.OrderId))
                .OrderBy(r => r.ExecutedAt)
                .Take(limit)
                .ToList();
        }
    }

    // 🔴 FR-10, ADR-0067 決定2, #1339, IADR-0539: 同じ建玉の判断由来の決済で、系が取消を求めた時刻が [from, to) にある件数。
    public int CountUnfilledCancelRequests(
        string symbol, Market market, TradeSide closeSide, DateTimeOffset fromInclusive, DateTimeOffset toExclusive)
    {
        ArgumentException.ThrowIfNullOrEmpty(symbol);

        lock (_gate)
        {
            return _records.Count(r => r.PositionEffect == PositionEffect.Close
                && r.ApprovalOrigin == OrderApprovalOrigin.TradeDecision
                && r.Symbol == symbol && r.Market == market && r.Side == closeSide
                && r.UnfilledCancelRequestedAt is { } at && at >= fromInclusive && at < toExclusive);
        }
    }

    // 🔴 FR-10, ADR-0067 決定1, #1339, IADR-0539: 取消を求めた印を書く（その値だけ。非終端かつ未印のときだけ）。
    public bool MarkUnfilledCancelRequested(string orderId, DateTimeOffset requestedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);

        lock (_gate)
        {
            var index = _records.FindIndex(r => r.OrderId == orderId);
            if (index < 0
                || _records[index].UnfilledCancelRequestedAt is not null
                || !OrderStatusLifecycle.IsPending(_records[index].Status))
            {
                return false;
            }

            _records[index] = _records[index] with { UnfilledCancelRequestedAt = requestedAt };
            return true;
        }
    }

    // 🔴 FR-10, FR-09, #1395, IADR-0560: S1 が取消を求めた印を書く（その値だけ。非終端のときだけ。既にあれば上書きしない）。
    public bool MarkSoftwareStopCancelRequested(string orderId, DateTimeOffset requestedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);

        lock (_gate)
        {
            var index = _records.FindIndex(r => r.OrderId == orderId);
            if (index < 0 || !OrderStatusLifecycle.IsPending(_records[index].Status))
                return false;
            if (_records[index].SoftwareStopCancelRequestedAt is not null)
                return true;

            _records[index] = _records[index] with { SoftwareStopCancelRequestedAt = requestedAt };
            return true;
        }
    }

    // 🔴 #1395, IADR-0560: S1 の取消が失敗したときに印を戻す（その値だけ。非終端のときだけ）。
    public bool ClearSoftwareStopCancelRequested(string orderId)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);

        lock (_gate)
        {
            var index = _records.FindIndex(r => r.OrderId == orderId);
            if (index < 0 || !OrderStatusLifecycle.IsPending(_records[index].Status))
                return false;

            _records[index] = _records[index] with { SoftwareStopCancelRequestedAt = null };
            return true;
        }
    }

    // #270, IADR-0113: 観測した最新状態を既存記録へ反映する（無ければ何もしない＝新規に作らない）。
    public bool UpdateOutcome(
        string orderId,
        OrderStatus status,
        int filledQuantity,
        decimal averagePrice,
        decimal slippageRatio,
        DateTimeOffset executedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);

        lock (_gate)
        {
            var index = _records.FindIndex(r => r.OrderId == orderId);
            if (index < 0)
                return false;

            _records[index] = _records[index] with
            {
                Status = status,
                FilledQuantity = filledQuantity,
                AveragePrice = averagePrice,
                SlippageRatio = slippageRatio,
                ExecutedAt = executedAt,
            };
            // 🔴 #1442, IADR-0573 決定2: 記録の更新と同じ排他の中で発行待ちの印を立てる。
            _publishPending.Add(orderId);
            return true;
        }
    }

    // 🔴 NFR, FR-05, FR-10, #1442, IADR-0573 決定2: 発行待ちの印が立った記録（古い順・上限つき。状態は問わない）。
    public IReadOnlyList<ExecutionRecord> FindOrderExecutedPublishPending(int batchSize)
    {
        lock (_gate)
        {
            return _records
                .Where(r => _publishPending.Contains(r.OrderId))
                .OrderBy(r => r.ExecutedAt)
                .Take(batchSize)
                .ToList();
        }
    }

    // 🔴 NFR, FR-05, FR-10, #1442, IADR-0573 決定2: 発行待ちの印を倒す（今の状態・約定数が発行したものと同じときだけ）。
    public bool MarkOrderExecutedPublished(string orderId, OrderStatus status, int filledQuantity)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);

        lock (_gate)
        {
            var index = _records.FindIndex(r => r.OrderId == orderId);
            if (index < 0 || !_publishPending.Contains(orderId)
                || _records[index].Status != status || _records[index].FilledQuantity != filledQuantity)
            {
                return false;
            }

            return _publishPending.Remove(orderId);
        }
    }
}
