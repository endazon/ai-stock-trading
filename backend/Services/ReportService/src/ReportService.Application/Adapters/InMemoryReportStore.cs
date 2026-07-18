using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Application.State;
using AiStockTrading.Report.Domain;

namespace AiStockTrading.Report.Application.Adapters;

// FR-06/07, IADR-0024: 報告書ストアのインメモリ実装（テスト・単体実行用）。PostgreSQL 永続化は Worker の EfReportStore で差し替える。
// PeriodKey ごとに 1 行＋Version。確定済みは不変。
public sealed class InMemoryReportStore : IReportStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (TradingReport Report, int Version, ReviewState Review)> _rows = new(StringComparer.Ordinal);

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
                // 新規/改訂ドラフトはレビュー局面 Drafting から始まる（IADR-0071 決定5）。
                _rows[report.PeriodKey] = (report, 1, ReviewState.Drafting);
                return 1;
            }

            if (existing.Report.State == ReportState.Confirmed)
                throw new InvalidOperationException($"確定済み報告書 {report.PeriodKey} は変更できません。");

            if (expectedVersion != existing.Version)
                throw new ReportConcurrencyException(report.PeriodKey, expectedVersion, existing.Version);

            var newVersion = existing.Version + 1;
            // 改訂（新ドラフト）はレビュー局面を Drafting へ戻す（対話的確定の Revise・IADR-0071 決定5）。
            _rows[report.PeriodKey] = (report, newVersion, ReviewState.Drafting);
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
            // 承認（対話的確定の Approve）でレビュー局面は Confirmed（終端）へ（IADR-0071 決定5）。
            _rows[periodKey] = (confirmed, existing.Version + 1, ReviewState.Confirmed);
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

    public ReportReview? GetReview(string periodKey)
    {
        lock (_gate)
        {
            return _rows.TryGetValue(periodKey, out var row)
                ? new ReportReview(periodKey, row.Review, row.Version)
                : null;
        }
    }

    public ReviewDecision? ApplyReview(string periodKey, ReviewCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            if (!_rows.TryGetValue(periodKey, out var row))
                return null;

            var decision = ReportReviewStateMachine.Decide(new ReportReview(periodKey, row.Review, row.Version), command);
            // 提示・差し戻しは内容不変（Version 不変）。遷移が受理されたときのみレビュー局面を更新する。
            if (decision.Accepted && decision.Transitioned)
                _rows[periodKey] = (row.Report, decision.Review.Version, decision.Review.State);

            return decision;
        }
    }
}
