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
    ReportAutoGenerationSettings settings,
    IReportDraftPresentedNotifier? notifier = null)
{
    /// <summary>1 巡回。生成境界を過ぎていて未生成の期間だけドラフトを生成し、提示（PendingApproval）まで進める。</summary>
    public async Task<ReportAutoGenerationResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var generated = new List<TradingReport>();
        var failed = new List<ReportAutoGenerationFailure>();
        var notPresented = new List<string>();
        var notificationFailed = new List<string>();

        foreach (var due in ReportSchedule.Due(clock.UtcNow, settings.Schedule))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 冪等: 既に行があるなら生成も提示もしない（利用者が手で作った・差し戻し中のドラフトを踏まない）。
            if (store.Get(due.PeriodKey) is not null)
                continue;

            try
            {
                var outcome = await GenerateAsync(due, cancellationToken).ConfigureAwait(false);
                generated.Add(outcome.Report);
                if (!outcome.Presented)
                    notPresented.Add(due.PeriodKey);
                if (outcome.NotificationFailed)
                    notificationFailed.Add(due.PeriodKey);

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

        return new ReportAutoGenerationResult(generated, failed, notPresented, notificationFailed);
    }

    private async Task<GenerationOutcome> GenerateAsync(DueReport due, CancellationToken cancellationToken)
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
        //
        // FR-07, IADR-0117 決定3, #293: 上位方針は **PeriodKey だけでなく本文まで**散文ドラフトへ渡す。
        // 従来は取得済みの parent から PeriodKey のみを使い PolicySummary を破棄していたため、計画
        // （04_workflows/03_reporting-cycle）が求める「上位方針の目標との差異評価」を LLM が書けなかった。
        // 渡し先は散文の文脈のみ。方針文（policy）へは混ぜない（IADR-0115 決定4・ADR-0003）。
        var draft = await draftService.BuildDraftAsync(
            new DraftRequest(
                due.Kind, due.PeriodKey, due.PeriodStart, settings.Markets, settings.AssumptionsVersion,
                parent?.Report.PeriodKey, policy, fills, CurrentPrices: null,
                ParentPolicySummary: parent?.Report.PolicySummary),
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
        // 直後の遷移のため通常は必ず受理されるが、ストア実装や状態機械の規則が変わったときに「提示が黙って失敗し
        // 承認待ちに並ばない」事故を検知できるよう、結果を捨てずに呼び出し側へ返す（常駐が警告ログに残す）。
        var decision = store.ApplyReview(due.PeriodKey, new ReviewCommand(ReviewAction.Present, settings.Actor, version));
        var presented = decision is { Accepted: true } && decision.Review.State == ReviewState.PendingApproval;

        // FR-09, IADR-0116 決定4: 提示通知の要約。数値はコード集計値のみ、散文はサニタイズ済み（Build の内側で適用）。
        var summary = ReportSummary.Build(due.Kind, ReportPeriod.Label(due.Kind, due.PeriodStart), draft.Pnl, draft.Narrative);

        // FR-09, IADR-0116 決定2: 提示まで到達したものだけ通知する（承認待ちに無いものを「確認してください」と言わない）。
        var notificationFailed = presented
            && !await NotifyAsync(due, summary, version, cancellationToken).ConfigureAwait(false);

        return new GenerationOutcome(report, presented, notificationFailed);
    }

    // FR-09, IADR-0116 決定2: 提示（確定依頼）の通知。best-effort であり、失敗しても生成・提示は巻き戻さない
    // （報告書は既に永続化され承認待ちに並んでいる）。通知の不達で報告書を作り直すほうが害が大きい。
    //
    // ただし**黙って捨てない**。失敗は戻り値で呼び出し側へ伝え、常駐が警告ログに残す（Failed・NotPresented と同じ扱い）。
    // 通知が届かないまま気付けない状態は、NotifyOnDraftPresented を既定 true にした理由（有効化したのに何も届かない
    // 状態を作らない）と正面から矛盾するため。Application 層をログ基盤へ依存させないため、ここではログを出さない。
    private async Task<bool> NotifyAsync(DueReport due, string summary, int version, CancellationToken cancellationToken)
    {
        if (notifier is null)
            return true; // 通知ポート未注入＝通知しない構成。失敗ではない。

        try
        {
            await notifier.NotifyAsync(
                new PresentedReportNotice(
                    due.PeriodKey, due.Kind, ReportPeriod.Label(due.Kind, due.PeriodStart), summary, version),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // 1 期間の生成結果（内部）。NotificationFailed は「提示はできたが通知の発行に失敗した」ことを表す。
    private sealed record GenerationOutcome(TradingReport Report, bool Presented, bool NotificationFailed);

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

// 1 巡回の結果。Generated は新規に生成した報告書、Failed は当該期間だけ落ちたもの（他期間は継続している）、
// NotPresented は生成できたが提示（PendingApproval への遷移）が受理されなかった期間（通常は空）、
// NotificationFailed は提示できたが確定依頼の通知に失敗した期間（通常は空）。
// いずれも常駐が警告ログに落とす＝異常を黙って捨てない（IADR-0115 決定1・IADR-0116 決定2）。
public sealed record ReportAutoGenerationResult(
    IReadOnlyList<TradingReport> Generated,
    IReadOnlyList<ReportAutoGenerationFailure> Failed,
    IReadOnlyList<string> NotPresented,
    IReadOnlyList<string> NotificationFailed);


// 期間単位の失敗（常駐側のログ出力に用いる）。
public sealed record ReportAutoGenerationFailure(string PeriodKey, Exception Error);
