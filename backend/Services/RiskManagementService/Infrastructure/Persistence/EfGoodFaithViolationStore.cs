using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Domain;
using Microsoft.EntityFrameworkCore;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-19, FR-10, FR-11, #425, ADR-0025 決定2, IADR-0165: GFV 自前計数台帳の EF 実装（追記専用）。
//
// **永続でなければならない。** 違反記録をプロセス内に持つと再起動で消え、「2 件で新規建てを止める」統制が
// **再起動 1 回で解ける**（fail-open）。口座種別の観測（IADR-0153 決定3・非永続）と設計が違うのは
// 「集計 vs 現在値」の違いである——違反件数は再観測で復元できない履歴である。
internal sealed class EfGoodFaithViolationStore(RiskManagementDbContext db) : IGoodFaithViolationStore
{
    public void Append(GoodFaithViolationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        // 冪等: 同一注文の 2 度目以降（部分約定の進行・メッセージ再送）は無視する。**件数が増えない側**である。
        if (db.GoodFaithViolations.Find(record.OrderId) is not null)
        {
            return;
        }

        db.GoodFaithViolations.Add(new GoodFaithViolationRow
        {
            OrderId = record.OrderId,
            Id = record.Id,
            DecisionId = record.DecisionId,
            Symbol = record.Symbol,
            Market = record.Market,
            PurchaseAmountInBase = record.PurchaseAmountInBase,
            SettledCashInBase = record.SettledCashInBase,
            OccurredOn = record.OccurredOn,
            ExecutedAtUtc = record.ExecutedAt,
            RecordedAtUtc = record.RecordedAt,
        });
        db.SaveChanges();
    }

    // **0 行でも「0 件を数えた」と返す**（台帳が権威である）。未供給（null）は本ストアが結線されていない
    // ときだけ生じ、その場合は判定コアが現金口座の新規建てを止める（IADR-0165 決定2）。
    //
    // **失効期間は設けない**（累計）。計画は「違反記録の失効」の期間も手段も定義しておらず、
    // 自動失効は fail-open であるため実装で値を発明しない（IADR-0165 決定4）。
    //
    // #464, ADR-0028 決定2, IADR-0182: **解除された記録は数えない。** ただし**行は消さない**——
    // 数えないことと消すことは別である（決定1「違反記録は失効させない」）。
    public GoodFaithViolationTally GetTally() =>
        GoodFaithViolationTally.Observed(UnclearedRows().Count());

    public IReadOnlyList<GoodFaithViolationRecord> GetRecordedBetween(DateOnly fromInclusive, DateOnly toInclusive) =>
        db.GoodFaithViolations
            .AsNoTracking()
            .Where(r => r.OccurredOn >= fromInclusive && r.OccurredOn <= toInclusive)
            .OrderBy(r => r.RecordedAtUtc)
            .ToList()
            .Select(Map)
            .ToList();

    // #464, ADR-0028 決定2, IADR-0182: まだ解除されていない違反記録（＝現在の計数の内訳）。
    public IReadOnlyList<GoodFaithViolationRecord> GetUncleared() =>
        UnclearedRows()
            .OrderBy(r => r.RecordedAtUtc)
            .ToList()
            .Select(Map)
            .ToList();

    // #464, ADR-0028 決定1/決定2, IADR-0182: 解除を**追記する**。
    //
    // 🔴 **違反記録の行を削除・更新しない。** 解除は別テーブルへの追記で表す。
    // 冪等: 同じ記録の 2 度目以降は無視する（**件数が増えも減りもしない側**）。
    public void AppendClearance(GoodFaithViolationClearance clearance)
    {
        ArgumentNullException.ThrowIfNull(clearance);

        if (db.GoodFaithViolationClearances.Find(clearance.OrderId) is not null)
        {
            return;
        }

        db.GoodFaithViolationClearances.Add(new GoodFaithViolationClearanceRow
        {
            OrderId = clearance.OrderId,
            ClearedBy = clearance.ClearedBy,
            Reason = clearance.Reason,
            ClearedAtUtc = clearance.ClearedAt,
        });
        db.SaveChanges();
    }

    // 解除済みを除いた違反記録の行。**解除は件数にしか作用しない**（GetRecordedBetween は全件を返す）。
    private IQueryable<GoodFaithViolationRow> UnclearedRows() =>
        db.GoodFaithViolations
            .AsNoTracking()
            .Where(v => !db.GoodFaithViolationClearances.Any(c => c.OrderId == v.OrderId));

    private static GoodFaithViolationRecord Map(GoodFaithViolationRow r) => new(
        r.Id, r.OrderId, r.DecisionId, r.Symbol, r.Market, r.PurchaseAmountInBase, r.SettledCashInBase,
        r.OccurredOn, r.ExecutedAtUtc, r.RecordedAtUtc);
}
