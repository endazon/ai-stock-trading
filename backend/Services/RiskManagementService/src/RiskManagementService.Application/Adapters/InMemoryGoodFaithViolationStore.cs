using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;

namespace AiStockTrading.RiskManagement.Application.Adapters;

// FR-19, FR-11, #425, ADR-0025 決定2, IADR-0165: GFV 自前計数台帳のインメモリ実装（ユニット試験・非 relational 用）。
//
// **本番は EF 実装（EfGoodFaithViolationStore）である。** 違反記録をプロセス内に持つと再起動で消え、
// 「2 件で新規建てを止める」統制が再起動 1 回で解ける（fail-open）ため、本実装を本番の既定にしてはならない。
public sealed class InMemoryGoodFaithViolationStore : IGoodFaithViolationStore
{
    private readonly Lock _gate = new();

    // **主キーは OrderId**（計上単位＝1 注文 1 件）。部分約定の進行・再送で二重計上しない。
    private readonly Dictionary<string, GoodFaithViolationRecord> _records = [];

    // #464, ADR-0028 決定1/決定2, IADR-0182: 解除の追記（**違反記録とは別の辞書である**——決定1 が
    // 「失効させない」と定めるため、解除で `_records` から消さない）。
    private readonly Dictionary<string, GoodFaithViolationClearance> _clearances = [];

    public void Append(GoodFaithViolationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_gate)
        {
            // 先着優先（同一注文の 2 度目以降は無視する）。件数が増えない側であることが冪等の要点である。
            _records.TryAdd(record.OrderId, record);
        }
    }

    public GoodFaithViolationTally GetTally()
    {
        lock (_gate)
        {
            // **0 行でも「0 件を数えた」と返す。** 台帳が権威であり、未供給（null）は
            // 「本ストアが結線されていない」ことだけを意味する（IADR-0165 決定2）。
            // #464: **解除された記録は数えない。ただし行は消さない**（数えないことと消すことは別である）。
            return GoodFaithViolationTally.Observed(
                _records.Keys.Count(orderId => !_clearances.ContainsKey(orderId)));
        }
    }

    public IReadOnlyList<GoodFaithViolationRecord> GetRecordedBetween(DateOnly fromInclusive, DateOnly toInclusive)
    {
        lock (_gate)
        {
            // **解除済みも含めて返す**（ADR-0028 決定1。証跡は消えない）。
            return _records.Values
                .Where(r => r.OccurredOn >= fromInclusive && r.OccurredOn <= toInclusive)
                .OrderBy(r => r.RecordedAt)
                .ToList();
        }
    }

    public IReadOnlyList<GoodFaithViolationRecord> GetUncleared()
    {
        lock (_gate)
        {
            return _records.Values
                .Where(r => !_clearances.ContainsKey(r.OrderId))
                .OrderBy(r => r.RecordedAt)
                .ToList();
        }
    }

    public void AppendClearance(GoodFaithViolationClearance clearance)
    {
        ArgumentNullException.ThrowIfNull(clearance);

        lock (_gate)
        {
            // 冪等（同じ記録の 2 度目以降は無視する）。EF 実装と同じ規律。
            _clearances.TryAdd(clearance.OrderId, clearance);
        }
    }
}
