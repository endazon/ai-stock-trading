using ReportService.Common.Abstractions;
using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-06/07, UC-03〜05, ADR-0003: 報告書のドラフト管理・版番号付き冪等確定・確定済み日報方針の照会。
// 確定は利用者のみ（アクター必須）。確定前の方針は取引に適用されない（ADR-0003）。数値集計・LLM ドラフトは後続スライス。
//
// FR-06, FR-07, UC-03, #839, IADR-0382: bootstrapNotifier は初回月報ブートストラップの提示通知（FR-09・IADR-0116）。
// **未注入は「通知経路が構成されていない」**＝提示はするが通知しない（自動生成の notifier と同じ扱い）。
public sealed class ReportAppService(
    IReportStore store,
    IClock clock,
    IReportDraftPresentedNotifier? bootstrapNotifier = null)
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

    /// <summary>
    /// FR-06, FR-07, UC-03, #839, IADR-0382: 初回月報ブートストラップを<b>保存して提示する</b>
    /// （承認待ちへ並べる）。確定は従来どおり利用者のみ（ADR-0003）——ここでは確定しない。
    /// <para>
    /// 🔴 <b>これが「初回の月報を作る導線」である。</b> <see cref="BuildMonthlyBootstrap"/>（<c>GET</c>）は
    /// ドラフトを返すだけで保存も提示もしないため、承認待ちに並ばず <c>/report approve</c> の対象にもならなかった
    /// （#839 の原因 2）。提示まで進めれば Discord の既存コマンドがそのまま使える。
    /// </para>
    /// <para>
    /// 🔴 <b>既存の行は踏まない。</b> 確定済み月報があれば不要（<see cref="MonthlyBootstrapOutcome.NotNeeded"/>）、
    /// 当月の行が既にあれば上書きしない（<see cref="MonthlyBootstrapOutcome.PeriodOccupied"/>）
    /// ——利用者が手で作ったドラフト・差し戻し中のドラフトを踏まないという自動生成の規則（IADR-0115 決定3）に揃える。
    /// </para>
    /// </summary>
    public async Task<MonthlyBootstrapResult> StartMonthlyBootstrapAsync(
        IReadOnlyList<string> watchlist,
        int assumptionsVersion,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(watchlist);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        if (BuildMonthlyBootstrap(watchlist, assumptionsVersion) is not { } draft)
            return new MonthlyBootstrapResult(MonthlyBootstrapOutcome.NotNeeded, null, 0, false, false);

        if (store.Get(draft.PeriodKey) is not null)
            return new MonthlyBootstrapResult(MonthlyBootstrapOutcome.PeriodOccupied, null, 0, false, false);

        var version = store.UpsertDraft(draft with { State = ReportState.Draft, ConfirmedAt = null }, expectedVersion: 0);

        // 提示（Drafting→PendingApproval）。自動生成と同じく**提示までで止める**（ADR-0003・IADR-0115 決定1）。
        var decision = store.ApplyReview(draft.PeriodKey, new ReviewCommand(ReviewAction.Present, actor, version));
        var presented = decision is { Accepted: true } && decision.Review.State == ReviewState.PendingApproval;

        // FR-09, IADR-0116: 提示まで到達したものだけ通知する（承認待ちに無いものを「確認してください」と言わない）。
        var notificationFailed = false;
        if (presented && bootstrapNotifier is not null)
        {
            try
            {
                await bootstrapNotifier.NotifyAsync(
                    new PresentedReportNotice(
                        draft.PeriodKey,
                        ReportKind.Monthly,
                        ReportPeriod.Label(ReportKind.Monthly, draft.PeriodStart),
                        MonthlyBootstrap.PresentationSummary(
                            ReportPeriod.Label(ReportKind.Monthly, draft.PeriodStart), draft.PolicySummary),
                        version),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 通知は best-effort。失敗しても保存・提示は巻き戻さない（自動生成と同じ規律）。
                notificationFailed = true;
            }
        }

        return new MonthlyBootstrapResult(
            MonthlyBootstrapOutcome.Started, draft, version, presented, notificationFailed);
    }
}

// FR-06, FR-07, UC-03, #839, IADR-0382: 初回月報ブートストラップの起動結果。
public enum MonthlyBootstrapOutcome
{
    /// <summary>保存し、提示（承認待ち）まで進めた。</summary>
    Started,

    /// <summary>確定済み月報が既にある＝ブートストラップは不要である。</summary>
    NotNeeded,

    /// <summary>当月の月報の行が既にある（手で作ったドラフト・差し戻し中）。<b>上書きしない。</b></summary>
    PeriodOccupied,
}

/// <param name="Outcome">結果の種別。</param>
/// <param name="Report">保存したドラフト（<see cref="MonthlyBootstrapOutcome.Started"/> のときだけ非 null）。</param>
/// <param name="Version">保存後の版番号（確定要求に添える <c>expectedVersion</c>）。</param>
/// <param name="Presented">承認待ちへ並んだか。<c>false</c> なら <c>/report approve</c> の対象にならない。</param>
/// <param name="NotificationFailed">
/// 提示はできたが通知を発行できなかったか。🔴 <b>成功に見せない</b>——利用者には「届かない」ことが見えている必要がある。
/// </param>
public sealed record MonthlyBootstrapResult(
    MonthlyBootstrapOutcome Outcome,
    TradingReport? Report,
    int Version,
    bool Presented,
    bool NotificationFailed);
