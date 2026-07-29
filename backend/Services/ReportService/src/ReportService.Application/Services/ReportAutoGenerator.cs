using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Domain;

namespace AiStockTrading.Report.Application.Services;

// FR-06/07/16, UC-03〜05, ADR-0003, 04_workflows/03_reporting-cycle, IADR-0115, #280:
// 日報/週報/月報の自動生成（1 巡回ぶん）。常駐（BackgroundService）から分離し、時刻を固定して単体テストできる単位にする。
//
// 自動化の終点は **提示（Present）** であり、確定（Confirm）はここから呼ばない。生成物は ReviewState.PendingApproval /
// ReportState.Draft で止まるため、取引が参照する「確定済み日報の方針」は利用者が確定するまで動かない
// （ADR-0003「完全無人での方針変更は行わない」・IADR-0115 決定1）。
//
// 冪等の根拠は PeriodKey の存在のみ（IADR-0115 決定3）。プロセス内に「生成済み」を持たないため、再起動・多重レプリカの
// いずれでも二重生成しない。1 期間の失敗は他の期間を巻き込まない（期間ごとに独立して捕捉する）。
public sealed class ReportAutoGenerator(
    IReportStore store,
    ReportDraftService draftService,
    IPeriodFillSource fillSource,
    IClock clock,
    ReportAutoGenerationSettings settings)
{
    /// <summary>1 巡回。生成境界を過ぎていて未生成の期間だけドラフトを生成し、提示（PendingApproval）まで進める。</summary>
    public async Task<ReportAutoGenerationResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var generated = new List<TradingReport>();
        var failed = new List<ReportAutoGenerationFailure>();

        foreach (var due in ReportSchedule.Due(clock.UtcNow, settings.Schedule))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 冪等: 既に行があるなら生成も提示もしない（利用者が手で作った・差し戻し中のドラフトを踏まない）。
            if (store.Get(due.PeriodKey) is not null)
                continue;

            try
            {
                generated.Add(await GenerateAsync(due, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw; // 停止要求は呼び出し側（常駐）へ伝える。
            }
            catch (ReportConcurrencyException)
            {
                // 他レプリカが同じ期間を先に作った（expectedVersion 0 の競合）。二重生成を避けた結果であり失敗ではない。
            }
            catch (InvalidOperationException)
            {
                // 直前に確定された等で upsert が拒否された。次巡回では PeriodKey 一致でスキップされる。
            }
            catch (Exception ex)
            {
                failed.Add(new ReportAutoGenerationFailure(due.PeriodKey, ex));
            }
        }

        return new ReportAutoGenerationResult(generated, failed);
    }

    private async Task<TradingReport> GenerateAsync(DueReport due, CancellationToken cancellationToken)
    {
        // 方針階層（03_reporting-cycle）: BasedOn は上位種別の直近確定済み。参照できなければ null＝方針文に明記する。
        var parentKind = ReportPolicyDraft.ParentKind(due.Kind);
        var parent = store.GetLatestConfirmed(parentKind);
        // 継続案の素は「同種別」の直近確定済み（月報は上位＝前月報と同一）。
        var previous = parentKind == due.Kind ? parent : store.GetLatestConfirmed(due.Kind);

        var policy = ReportPolicyDraft.CarryOver(
            due.Kind, previous?.Report.PeriodKey, previous?.Report.PolicySummary, parent?.Report.PeriodKey);

        var fills = await SafeFillsAsync(due, cancellationToken).ConfigureAwait(false);

        // 数値はコード集計・散文は LLM ドラフト（IADR-0032）。現在値は要求で指定せず、市場データ源へ委ねる（IADR-0066）。
        var draft = await draftService.BuildDraftAsync(
            new DraftRequest(
                due.Kind, due.PeriodKey, due.PeriodStart, settings.Markets, settings.AssumptionsVersion,
                parent?.Report.PeriodKey, policy, fills, CurrentPrices: null),
            cancellationToken).ConfigureAwait(false);

        var report = new TradingReport
        {
            PeriodKey = due.PeriodKey,
            Kind = due.Kind,
            PeriodStart = due.PeriodStart,
            BasedOn = parent?.Report.PeriodKey,
            AssumptionsVersion = settings.AssumptionsVersion,
            PolicySummary = policy,
            Body = draft.Markdown,
        };

        var version = store.UpsertDraft(report, expectedVersion: 0);

        // 提示（Drafting→PendingApproval）。承認・確定は利用者の OwnerOnly 経路のみ（ADR-0003・IADR-0115 決定1）。
        store.ApplyReview(due.PeriodKey, new ReviewCommand(ReviewAction.Present, settings.Actor, version));

        return report;
    }

    // 供給不達は空列へ倒す（IADR-0115 決定5）。報告書は発注判断を行わないため、欠測が過大発注へ繋がる経路が無く、
    // 「数値 0 のドラフトを提示して気付かせる」ほうが「何も出さない」より安全である。
    private async Task<IReadOnlyList<PeriodTradeFill>> SafeFillsAsync(DueReport due, CancellationToken cancellationToken)
    {
        try
        {
            return await fillSource.GetFillsAsync(due.PeriodStart, due.PeriodEnd, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }
}

// FR-06, IADR-0115: 自動生成の構成。生成境界（JST）と休場日は ReportScheduleOptions（Domain・純関数の入力）へ委ねる。
public sealed record ReportAutoGenerationSettings
{
    /// <summary>生成境界（JST）と休場日。</summary>
    public ReportScheduleOptions Schedule { get; init; } = new();

    /// <summary>フロントマターの対象市場表記（"JP"/"US" 等）。既定は空。</summary>
    public IReadOnlyList<string> Markets { get; init; } = [];

    /// <summary>適用する全体前提条件のバージョン（FR-17）。既定 1。</summary>
    public int AssumptionsVersion { get; init; } = 1;

    /// <summary>提示（Present）の操作者。状態機械が actor 必須のため設定する（HTTP の OwnerOnly 認可は不変）。</summary>
    public string Actor { get; init; } = "report-scheduler";
}

// 1 巡回の結果。Generated は新規に生成・提示した報告書、Failed は当該期間だけ落ちたもの（他期間は継続している）。
public sealed record ReportAutoGenerationResult(
    IReadOnlyList<TradingReport> Generated,
    IReadOnlyList<ReportAutoGenerationFailure> Failed);

// 期間単位の失敗（常駐側のログ出力に用いる）。
public sealed record ReportAutoGenerationFailure(string PeriodKey, Exception Error);
