using ReportService.Common.Exceptions;
using ReportService.Features.Reports;
using ReportService.Domain;

namespace ReportService.Infrastructure.Persistence;

// FR-06/07, IADR-0024: 報告書ストアのインメモリ実装（テスト・単体実行用）。PostgreSQL 永続化は Worker の EfReportStore で差し替える。
// PeriodKey ごとに 1 行＋Version。確定済みは不変。
// ［2026-10-17 / #1373・IADR-0549 決定1］確定した版の写しは不変。行は、効いている確定済み日報に限り StartRevisionDraft で版 n+1 のドラフトへ戻せる（UpsertDraft は従来どおり確定済みを拒む）。
public sealed class InMemoryReportStore : IReportStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (TradingReport Report, int Version, ReviewState Review)> _rows = new(StringComparer.Ordinal);

    // FR-07, ADR-0068 決定2, #1373, IADR-0548 決定1: 確定した版の写し（確定のたびに 1 件。書き換えない。EfReportStore と同じ）。
    private readonly List<VersionedReport> _confirmedVersions = [];

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

    // FR-14, #843 項目1, IADR-0418: EfReportStore と同じ並び（開始日の降順・同日は会話キーの降順）。
    public IReadOnlyList<ReportPeriodKeyItem> ListPeriodKeys()
    {
        lock (_gate)
        {
            return [.. _rows.Values
                .Select(r => new ReportPeriodKeyItem(r.Report.PeriodKey, r.Report.PeriodStart))
                .OrderByDescending(r => r.PeriodStart)
                .ThenByDescending(r => r.PeriodKey, StringComparer.Ordinal)];
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
                throw new ReportAlreadyConfirmedException(report.PeriodKey);

            if (expectedVersion != existing.Version)
                throw new ReportConcurrencyException(report.PeriodKey, expectedVersion, existing.Version);

            var newVersion = existing.Version + 1;
            // FR-06, IADR-0115: 本文（Markdown）は明示的に非空を渡されたときだけ差し替える（EfReportStore と同じ規則）。
            // 手動 upsert（PUT /reports/{periodKey}）は本文を持たないため、空で上書きして生成済みの本文を消さない。
            // #840, IADR-0352 決定 5: **未供給の記録は本文に従う**。本文を差し替えない改訂では、その本文が
            // どの入力を欠いて生成されたかの記録も残す（本文だけ残って警告が消える、を作らない）。
            var merged = string.IsNullOrEmpty(report.Body)
                ? report with { Body = existing.Report.Body, UnsuppliedInputs = existing.Report.UnsuppliedInputs }
                : report;
            // 改訂（新ドラフト）はレビュー局面を Drafting へ戻す（対話的確定の Revise・IADR-0071 決定5）。
            _rows[report.PeriodKey] = (merged, newVersion, ReviewState.Drafting);
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
                return new ConfirmResult(existing.Report, Transitioned: false, existing.Version);

            if (expectedVersion != existing.Version)
                throw new ReportConcurrencyException(periodKey, expectedVersion, existing.Version);

            // ADR-0068 決定1, IADR-0549 決定2: 版 n+1 の確定（再確定）は、なお効いているときだけ（EfReportStore と同じ規則・同じ排他の中）。
            var superseded = LatestOfKey(periodKey)?.Version;
            if (superseded is not null)
            {
                var effective = SelectLatest(_confirmedVersions.Where(v => v.Report.Kind == existing.Report.Kind));
                if (existing.Report.Kind != ReportKind.Daily || effective?.Report.PeriodKey != periodKey)
                    throw new ReportNoLongerEffectiveException(periodKey, effective?.Report.PeriodKey);
            }

            // #338, #310, INDEX 決定29: **本文の状態表記も確定へ揃える**（EfReportStore と同じ規則）。
            // 本文は生成時に一度だけ組み立てられるため、ここで揃えないと確定済みの報告書が
            // frontmatter と YAML ブロックで「未確定」を名乗り続ける（取引判断は YAML を読む）。
            var confirmed = existing.Report with
            {
                State = ReportState.Confirmed,
                ConfirmedAt = confirmedAt,
                Body = ReportBodyStatus.MarkConfirmed(existing.Report.Body, confirmedAt) ?? string.Empty,
            };
            // 承認（対話的確定の Approve）でレビュー局面は Confirmed（終端）へ（IADR-0071 決定5）。
            _rows[periodKey] = (confirmed, existing.Version + 1, ReviewState.Confirmed);
            // ADR-0068 決定2, IADR-0548 決定1: 確定した版の写しを足す（行の確定と同じ排他の中＝1 操作）。
            _confirmedVersions.Add(new VersionedReport(confirmed, existing.Version + 1));
            return new ConfirmResult(confirmed, Transitioned: true, existing.Version + 1, superseded);
        }
    }

    // FR-07, FR-14, ADR-0068 決定1, #1373, IADR-0549 決定1: 効いている確定済み日報に版を足すドラフト（EfReportStore と同じ規則）。
    public int StartRevisionDraft(TradingReport draft, int expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(draft);
        lock (_gate)
        {
            if (!_rows.TryGetValue(draft.PeriodKey, out var existing))
                throw new ReportConcurrencyException(draft.PeriodKey, expectedVersion, 0);
            if (existing.Report.State != ReportState.Confirmed || expectedVersion != existing.Version)
                throw new ReportConcurrencyException(draft.PeriodKey, expectedVersion, existing.Version);

            var effective = SelectLatest(_confirmedVersions.Where(v => v.Report.Kind == ReportKind.Daily));
            if (existing.Report.Kind != ReportKind.Daily || effective?.Report.PeriodKey != draft.PeriodKey)
                throw new ReportNoLongerEffectiveException(draft.PeriodKey, effective?.Report.PeriodKey);

            var reopened = existing.Report with
            {
                PolicySummary = draft.PolicySummary,
                Body = string.IsNullOrEmpty(draft.Body) ? existing.Report.Body : draft.Body,
                UnsuppliedInputs = string.IsNullOrEmpty(draft.Body) ? existing.Report.UnsuppliedInputs : draft.UnsuppliedInputs,
                State = ReportState.Draft,
                ConfirmedAt = null,
            };
            _rows[draft.PeriodKey] = (reopened, existing.Version + 1, ReviewState.Drafting);
            return existing.Version + 1;
        }
    }

    public VersionedReport? GetLatestConfirmedVersion(string periodKey)
    {
        lock (_gate)
        {
            return LatestOfKey(periodKey);
        }
    }

    public IReadOnlyList<VersionedReport> ListLatestConfirmedVersions()
    {
        lock (_gate)
        {
            return [.. _confirmedVersions
                .GroupBy(v => v.Report.PeriodKey, StringComparer.Ordinal)
                .Select(g => g.OrderByDescending(v => v.Report.ConfirmedAt).ThenByDescending(v => v.Version).First())];
        }
    }

    // 呼び出し側が _gate を持っていること。
    private VersionedReport? LatestOfKey(string periodKey) =>
        _confirmedVersions
            .Where(v => string.Equals(v.Report.PeriodKey, periodKey, StringComparison.Ordinal))
            .OrderByDescending(v => v.Report.ConfirmedAt)
            .ThenByDescending(v => v.Version)
            .FirstOrDefault();

    // FR-07, ADR-0068 決定2, #1373, IADR-0548 決定3: 確定した版の写しから二段で選ぶ（EfReportStore と同じ規則）。
    public VersionedReport? GetLatestConfirmed(ReportKind kind)
    {
        lock (_gate)
        {
            return SelectLatest(_confirmedVersions.Where(v => v.Report.Kind == kind));
        }
    }

    // FR-15, FR-07, #1308, #1373, IADR-0528 決定3, IADR-0548 決定4: at の時点で確定していた版だけを対象に、同じ二段で選ぶ。
    public VersionedReport? GetLatestConfirmedAsOf(ReportKind kind, DateTimeOffset at)
    {
        lock (_gate)
        {
            return SelectLatest(_confirmedVersions.Where(v => v.Report.Kind == kind
                && v.Report.ConfirmedAt is { } confirmedAt
                && confirmedAt <= at));
        }
    }

    // ADR-0068 決定2, IADR-0548 決定3: 開始日（同日は会話キーの降順）で期間キーを選び、その中で確定時刻・版の大きい方。
    private static VersionedReport? SelectLatest(IEnumerable<VersionedReport> versions) =>
        versions
            .OrderByDescending(v => v.Report.PeriodStart)
            .ThenByDescending(v => v.Report.PeriodKey, StringComparer.Ordinal)
            .ThenByDescending(v => v.Report.ConfirmedAt)
            .ThenByDescending(v => v.Version)
            .FirstOrDefault();

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
