using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Application.State;
using AiStockTrading.Report.Domain;

namespace AiStockTrading.Report.Application.Adapters;

// FR-06/07, IADR-0024: 報告書ストアのインメモリ実装（テスト・単体実行用）。PostgreSQL 永続化は Worker の EfReportStore で差し替える。
// PeriodKey ごとに 1 行＋Version。確定済みは不変。
public sealed class InMemoryReportStore : IReportStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (TradingReport Report, int Version)> _rows = new(StringComparer.Ordinal);

    public VersionedReport? Get(string periodKey)
    {
        lock (_gate)
        {
            return _rows.TryGetValue(periodKey, out var row) ? new VersionedReport(row.Report, row.Version) : null;
        }
    }

    public IReadOnlyList<TradingReport> List()
    {
        lock (_gate)
        {
            return [.. _rows.Values.Select(r => r.Report)];
        }
    }

    public int UpsertDraft(TradingReport report, int expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            if (!_rows.TryGetValue(report.PeriodKey, out var existing))
            {
                if (expectedVersion != 0)
                    throw new ReportConcurrencyException(report.PeriodKey, expectedVersion, 0);
                _rows[report.PeriodKey] = (report, 1);
                return 1;
            }

            if (existing.Report.State == ReportState.Confirmed)
                throw new InvalidOperationException($"確定済み報告書 {report.PeriodKey} は変更できません。");

            if (expectedVersion != existing.Version)
                throw new ReportConcurrencyException(report.PeriodKey, expectedVersion, existing.Version);

            var newVersion = existing.Version + 1;
            _rows[report.PeriodKey] = (report, newVersion);
            return newVersion;
        }
    }

    public ConfirmResult? Confirm(string periodKey, int expectedVersion, DateTimeOffset confirmedAt)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(periodKey, out var existing))
                return null;

            // 既に確定済みなら冪等（状態変化なし・イベント発行なし）。
            if (existing.Report.State == ReportState.Confirmed)
                return new ConfirmResult(existing.Report, Transitioned: false);

            if (expectedVersion != existing.Version)
                throw new ReportConcurrencyException(periodKey, expectedVersion, existing.Version);

            var confirmed = existing.Report with { State = ReportState.Confirmed, ConfirmedAt = confirmedAt };
            _rows[periodKey] = (confirmed, existing.Version + 1);
            return new ConfirmResult(confirmed, Transitioned: true);
        }
    }

    public VersionedReport? GetLatestConfirmed(ReportKind kind)
    {
        lock (_gate)
        {
            var latest = _rows.Values
                .Where(r => r.Report.Kind == kind && r.Report.State == ReportState.Confirmed)
                .OrderByDescending(r => r.Report.PeriodStart)
                .Select(r => new VersionedReport(r.Report, r.Version))
                .FirstOrDefault();
            return latest;
        }
    }
}
