using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Application.State;
using AiStockTrading.Report.Domain;

namespace AiStockTrading.Report.Application.Services;

// FR-06/07, UC-03〜05, ADR-0007: 報告書のドラフト管理・版番号付き冪等確定・確定済み日報方針の照会。
// 確定は利用者のみ（アクター必須）。確定前の方針は取引に適用されない（ADR-0003）。数値集計・LLM ドラフトは後続スライス。
public sealed class ReportService(IReportStore store, IClock clock)
{
    public VersionedReport? Get(string periodKey) => store.Get(periodKey);

    public IReadOnlyList<TradingReport> List() => store.List();

    /// <summary>ドラフトを作成/更新し、確定後の Version を返す。</summary>
    public int UpsertDraft(TradingReport report, int expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(report.PeriodKey);

        // 確定済みは不変。ドラフトとして upsert する（状態は Draft に固定）。
        return store.UpsertDraft(report with { State = ReportState.Draft, ConfirmedAt = null }, expectedVersion);
    }

    /// <summary>確定する（Draft→Confirmed）。利用者のみ。既に確定済みは冪等（Transitioned=false）。対象が無ければ null。</summary>
    public ConfirmResult? Confirm(string periodKey, int expectedVersion, string actor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        return store.Confirm(periodKey, expectedVersion, clock.UtcNow);
    }

    /// <summary>最新の確定済み日報の方針（取引判断の IDailyPolicyProvider 実データ源）。未確定なら null。</summary>
    public ConfirmedDailyPolicy? GetConfirmedDailyPolicy()
    {
        var latest = store.GetLatestConfirmed(ReportKind.Daily);
        return latest is null
            ? null
            : new ConfirmedDailyPolicy(latest.Report.PeriodStart, latest.Report.PolicySummary, latest.Report.AssumptionsVersion);
    }
}
