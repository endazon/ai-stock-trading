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
            return GoodFaithViolationTally.Observed(_records.Count);
        }
    }

    public IReadOnlyList<GoodFaithViolationRecord> GetRecordedBetween(DateOnly fromInclusive, DateOnly toInclusive)
    {
        lock (_gate)
        {
            return _records.Values
                .Where(r => r.OccurredOn >= fromInclusive && r.OccurredOn <= toInclusive)
                .OrderBy(r => r.RecordedAt)
                .ToList();
        }
    }
}
