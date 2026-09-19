using ReportService.Common.Abstractions;
using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-06/07, UC-03〜05, ADR-0003: 報告書のドラフト管理・版番号付き冪等確定・確定済み日報方針の照会。
// 確定は利用者のみ（アクター必須）。確定前の方針は取引に適用されない（ADR-0003）。数値集計・LLM ドラフトは後続スライス。
public sealed class ReportAppService(IReportStore store, IClock clock)
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

    /// <summary>FR-07, IADR-0042/0071 決定5: 対話的確定のレビュー局面を取得する。対象が無ければ null。</summary>
    public ReportReview? GetReview(string periodKey) => store.GetReview(periodKey);

    /// <summary>
    /// FR-07, FR-14, #840, IADR-0352 決定 5: レビュー局面に**未供給だった入力の表示名**を添えた照会用の射影。
    /// Bot（<c>/report show</c>・確認ボタンの前段）が確定の前に欠落を見せるために使う。対象が無ければ null。
    /// <para>
    /// 🔴 本文・要約は載せない（IADR-0240 決定4: サニタイズ済みの通知経路を迂回する経路を作らない）。
    /// 載せるのは <see cref="ReportInputs.Label"/> の**コード定数**だけであり、外部入力を含まない。
    /// </para>
    /// </summary>
    public ReportReviewView? GetReviewView(string periodKey)
    {
        var review = store.GetReview(periodKey);
        if (review is null)
            return null;

        var unsupplied = store.Get(periodKey)?.Report.UnsuppliedInputs ?? [];
        return new ReportReviewView(review.PeriodKey, review.State, review.Version, ReportInputs.Labels(unsupplied));
    }

    /// <summary>
    /// FR-07, IADR-0042/0071 決定5: 対話的確定のレビュー操作（提示・差し戻し）を適用する。利用者のみ（actor 必須）。
    /// 版番号付きの楽観排他・不正遷移・確定済み変更は状態機械が拒否し、拒否理由を含む決定を返す。対象が無ければ null。
    /// </summary>
    public ReviewDecision? ApplyReview(string periodKey, ReviewAction action, int expectedVersion, string actor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        return store.ApplyReview(periodKey, new ReviewCommand(action, actor, expectedVersion));
    }

    /// <summary>最新の確定済み日報の方針（取引判断の IDailyPolicyProvider 実データ源）。未確定なら null。</summary>
    public ConfirmedDailyPolicy? GetConfirmedDailyPolicy()
    {
        var latest = store.GetLatestConfirmed(ReportKind.Daily);
        return latest is null
            ? null
            : new ConfirmedDailyPolicy(latest.Report.PeriodStart, latest.Report.PolicySummary, latest.Report.AssumptionsVersion);
    }

    /// <summary>
    /// FR-06, UC-03, IADR-0071 決定4: 初回月報ブートストラップ。確定済み月報がまだ無いとき（＝運用開始直後）に、当月の初期監視銘柄を
    /// 選定した月報ドラフトを返す。既に確定済み月報があればブートストラップ不要として null を返す。生成のみ（永続化しない）。
    /// </summary>
    public TradingReport? BuildMonthlyBootstrap(IReadOnlyList<string> watchlist, int assumptionsVersion)
    {
        ArgumentNullException.ThrowIfNull(watchlist);

        if (store.GetLatestConfirmed(ReportKind.Monthly) is not null)
            return null; // 既にブートストラップ済み（確定済み月報が存在する）。

        var month = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        return MonthlyBootstrap.BuildDraft(month, watchlist, assumptionsVersion);
    }
}
