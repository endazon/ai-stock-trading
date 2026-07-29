using AiStockTrading.Report.Application;
using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Application.State;
using AiStockTrading.Report.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.Report.Worker.Foundation.Persistence;

// FR-06/07, IADR-0012/0024: 報告書ストアの EF 実装。PeriodKey ごとに 1 行＋Version 楽観排他。確定済みは不変。
internal sealed class EfReportStore(ReportDbContext db) : IReportStore
{
    public VersionedReport? Get(string periodKey)
    {
        var row = db.Reports.Find(periodKey);
        return row is null ? null : new VersionedReport(ToReport(row), row.Version);
    }

    public IReadOnlyList<TradingReport> List() =>
        [.. db.Reports.OrderByDescending(r => r.PeriodStart).Select(r => ToReport(r))];

    public int UpsertDraft(TradingReport report, int expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(report);

        var row = db.Reports.Find(report.PeriodKey);
        if (row is null)
        {
            if (expectedVersion != 0)
                throw new ReportConcurrencyException(report.PeriodKey, expectedVersion, 0);

            db.Reports.Add(new ReportRow
            {
                PeriodKey = report.PeriodKey,
                Kind = report.Kind,
                PeriodStart = report.PeriodStart,
                State = ReportState.Draft,
                // 新規/改訂ドラフトはレビュー局面 Drafting から始まる（IADR-0071 決定5）。
                ReviewState = ReviewState.Drafting,
                BasedOn = report.BasedOn,
                AssumptionsVersion = report.AssumptionsVersion,
                PolicySummary = report.PolicySummary,
                // FR-06, IADR-0115: 本文（Markdown）は自動生成経路のみが持つ。空なら NULL のまま。
                Body = string.IsNullOrEmpty(report.Body) ? null : report.Body,
                ConfirmedAt = null,
                Version = 1,
            });
            try
            {
                db.SaveChanges();
                return 1;
            }
            catch (DbUpdateException)
            {
                // 同一 PeriodKey の並行作成による一意制約違反。競合として扱う（他リクエストが作成済み）。
                db.ChangeTracker.Clear();
                var current = db.Reports.Find(report.PeriodKey);
                throw new ReportConcurrencyException(report.PeriodKey, expectedVersion, current?.Version ?? 0);
            }
        }

        if (row.State == ReportState.Confirmed)
            throw new InvalidOperationException($"確定済み報告書 {report.PeriodKey} は変更できません。");

        if (expectedVersion != row.Version)
            throw new ReportConcurrencyException(report.PeriodKey, expectedVersion, row.Version);

        row.Kind = report.Kind;
        row.PeriodStart = report.PeriodStart;
        row.BasedOn = report.BasedOn;
        row.AssumptionsVersion = report.AssumptionsVersion;
        row.PolicySummary = report.PolicySummary;
        // FR-06, IADR-0115: 本文は明示的に非空を渡されたときだけ差し替える。手動 upsert（PUT /reports/{periodKey}）は
        // 本文を持たないため、空で上書きして生成済みの Markdown を消さない。
        if (!string.IsNullOrEmpty(report.Body))
            row.Body = report.Body;
        row.State = ReportState.Draft;
        // 改訂（新ドラフト）はレビュー局面を Drafting へ戻す（対話的確定の Revise・IADR-0071 決定5）。
        row.ReviewState = ReviewState.Drafting;
        row.ConfirmedAt = null;
        row.Version += 1;
        db.SaveChanges();
        return row.Version;
    }

    public ConfirmResult? Confirm(string periodKey, int expectedVersion, DateTimeOffset confirmedAt)
    {
        var row = db.Reports.Find(periodKey);
        if (row is null)
            return null;

        // 既に確定済みなら冪等（状態変化なし・イベント発行なし）。
        if (row.State == ReportState.Confirmed)
            return new ConfirmResult(ToReport(row), Transitioned: false);

        if (expectedVersion != row.Version)
            throw new ReportConcurrencyException(periodKey, expectedVersion, row.Version);

        row.State = ReportState.Confirmed;
        // 承認（対話的確定の Approve）でレビュー局面は Confirmed（終端）へ（IADR-0071 決定5）。
        row.ReviewState = ReviewState.Confirmed;
        row.ConfirmedAt = confirmedAt;
        row.Version += 1;
        db.SaveChanges();
        return new ConfirmResult(ToReport(row), Transitioned: true);
    }

    public ReportReview? GetReview(string periodKey)
    {
        var row = db.Reports.Find(periodKey);
        return row is null ? null : new ReportReview(periodKey, row.ReviewState, row.Version);
    }

    public ReviewDecision? ApplyReview(string periodKey, ReviewCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var row = db.Reports.Find(periodKey);
        if (row is null)
            return null;

        var decision = ReportReviewStateMachine.Decide(new ReportReview(periodKey, row.ReviewState, row.Version), command);
        // 提示・差し戻しは内容不変（Version 不変）。遷移が受理されたときのみレビュー局面を更新する。
        if (decision.Accepted && decision.Transitioned)
        {
            row.ReviewState = decision.Review.State;
            db.SaveChanges();
        }

        return decision;
    }

    public VersionedReport? GetLatestConfirmed(ReportKind kind)
    {
        var row = db.Reports
            .Where(r => r.Kind == kind && r.State == ReportState.Confirmed)
            .OrderByDescending(r => r.PeriodStart)
            .FirstOrDefault();
        return row is null ? null : new VersionedReport(ToReport(row), row.Version);
    }

    private static TradingReport ToReport(ReportRow r) => new()
    {
        PeriodKey = r.PeriodKey,
        Kind = r.Kind,
        PeriodStart = r.PeriodStart,
        State = r.State,
        BasedOn = r.BasedOn,
        AssumptionsVersion = r.AssumptionsVersion,
        PolicySummary = r.PolicySummary,
        Body = r.Body ?? string.Empty,
        ConfirmedAt = r.ConfirmedAt,
    };
}
