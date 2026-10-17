using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Logging;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using ReportService.Infrastructure.ExternalServices;
using Wolverine;
using AppSvc = ReportService.Features.Reports.ReportAppService;

namespace ReportService.Features.Reports.ConfirmReport;

internal static class ConfirmReportEndpoint
{
    // 確定（Draft→Confirmed・利用者のみ・版番号付き冪等）。遷移時のみ ReportConfirmed を発行し、確定報告書を KB へ保存する。
    // 対象が無ければ 404。KB 保存は best-effort（既定 no-op・fail-safe＝確定を壊さない・FR-08/IADR-0071 決定3）。
    //
    // FR-09, UC-03, ADR-0003, IADR-0240 決定11, #774: **確定者は ConfirmingActorResolver が決める。** Discord Bot は
    // owner マップ機密クライアントのトークンで呼ぶため、トークンの主体は人ではない。本文の OnBehalfOf（代理される
    // 利用者）は**信頼するクライアントのトークンに限って**採り、利用者トークン直叩きでは無視する（なりすまし防止）。
    public static void MapConfirmReport(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/{periodKey}/confirm", (string periodKey, ConfirmReportRequest req, AppSvc svc,
            IMessageBus bus, IKnowledgeBaseWriter kb, ILoggerFactory loggerFactory, DelegatedActorOptions delegated,
            IPolicyRevisionLedger policyLedger, IReportDraftKnowledgeCopy draftCopy, HttpContext http) =>
                HandleAsync(periodKey, req, svc, bus, kb, loggerFactory, delegated, policyLedger, http, draftCopy));

    // NFR, IADR-0450, #753（段 5）: REST と gRPC 面（ReportOwnerWriteGrpcService）が共有する処理（確定者の解決・版番号付きの冪等・監査の発行・
    // KB への保存・台帳の確定時刻を 2 箇所に書かない）。
    internal static async Task<IResult> HandleAsync(string periodKey, ConfirmReportRequest req, AppSvc svc,
        IMessageBus bus, IKnowledgeBaseWriter kb, ILoggerFactory loggerFactory, DelegatedActorOptions delegated,
        IPolicyRevisionLedger policyLedger, HttpContext http, IReportDraftKnowledgeCopy? draftCopy = null)
    {
        var confirming = ConfirmingActorResolver.Resolve(http.User, req.OnBehalfOf, delegated.TrustedClientIds);
        var actorLogger = loggerFactory.CreateLogger("ReportConfirmingActor");

        // 信頼クライアントの代理指定が値域外。**確定者を記録できない確定は行わない**（状態にも触れない）。
        if (confirming.Rejected)
        {
            actorLogger.LogWarning(
                "確定要求の代理される利用者（OnBehalfOf）が値域外のため確定を拒否しました（PeriodKey={PeriodKey}）。",
                LogSanitizer.Sanitize(periodKey));
            return Results.BadRequest(new { error = "代理される利用者（onBehalfOf）の形式が不正です。" });
        }

        // 代理指定を信じなかった（利用者トークン直叩き・一覧外のクライアント・一覧未設定）。確定は通すが、
        // なりすましの試行／設定漏れのどちらも見えるよう警告に残す。
        if (confirming.IgnoredOnBehalfOf)
        {
            actorLogger.LogWarning(
                "確定要求の OnBehalfOf を無視しました（信頼するクライアントのトークンではありません。"
                + "PeriodKey={PeriodKey}・確定者={Actor}）。",
                LogSanitizer.Sanitize(periodKey), LogSanitizer.Sanitize(confirming.Actor));
        }

        var actor = confirming.Actor;
        // FR-07, FR-08, ADR-0068 決定6, #1373, IADR-0549 決定5: 確定の前に効いている日報を覚える（日報の確定で移ったら、
        // 移る前の日報に残った版 n+1 のドラフトの写しへ失効の印を付ける）。
        var effectiveBefore = svc.EffectiveDailyPeriodKey();
        var result = svc.Confirm(periodKey, req.ExpectedVersion, actor);
        if (result is null)
            return Results.NotFound();

        if (result.Transitioned)
        {
            var r = result.Report;
            // FR-07, FR-09, ADR-0068 決定6, #1373, IADR-0549 決定6: 確定した版と、再確定ならそれまで効いていた版（通知が「効いている版が変わった」と書く）。
            await bus.PublishAsync(new ReportConfirmed(
                r.PeriodKey, r.Kind.ToString(), actor, r.AssumptionsVersion, r.ConfirmedAt ?? DateTimeOffset.UtcNow,
                confirming.AuthorizedBy, result.Version, result.SupersededVersion));

            // FR-13, ADR-0042 決定 1, #1025, IADR-0433 決定 7（PR #1027 の監査 M1）: 確定された版が `/policy` の案なら、
            // 台帳に「この版で確定された」時刻を残す（適用の内訳が無いままなら「確定されたが適用を試みていない」が台帳で見える）。
            // best-effort（確定を壊さない）。
            // FR-07, #1385, IADR-0556 決定 1: KB の後処理より**前**に置く（KB の遅さ・時間予算の打ち切りに左右させない。順序の拘束は無い）。
            try
            {
                if (policyLedger.FindProposed(r.PeriodKey, result.Version - 1) is { } attempt)
                    policyLedger.MarkProposalConfirmed(attempt.Id, r.ConfirmedAt ?? DateTimeOffset.UtcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                loggerFactory.CreateLogger("ReportPolicyRevisionLedger")
                    .LogWarning(ex, "確定した版の方針の改訂の試行に確定の時刻を残せませんでした（{PeriodKey}）。", LogSanitizer.Sanitize(r.PeriodKey));
            }

            await RunKnowledgeAftercareAsync(r, result, actor, effectiveBefore, svc, kb, draftCopy, loggerFactory, http).ConfigureAwait(false);
        }

        // FR-07, FR-13, #1025, IADR-0433 決定 7（PR #1027 の監査 H1）: 報告書の項目に `transitioned`（この要求で確定したか）と
        // `version`（確定後の版）を足して返す（項目の追加だけ＝既存の読み手は壊れない）。冪等な再確定を「版 N を確定した」と
        // 読み違えないため、呼び出し側は version == expectedVersion + 1 で「この版で確定されている」を見分ける。
        return Results.Ok(ConfirmReportResponse.From(result));
    }

    // FR-07, FR-08, ADR-0068 決定6, #1385, IADR-0556 決定 1: 確定の後の KB の後処理（確定版の写しの保存／置き換え → 失効の印 → ドラフトの写しの削除）。
    // 🔴 **1 つの時間予算（`Reports:Confirm:KnowledgeBudgetSeconds`・既定 20 秒）で打ち切る** —— 確定の応答の所要の上限を決め、
    // 呼び出し側（通知サービス）の上限（既定 30 秒）を内側より長く保つため（外側＞内側）。予算は要求の取り消しと結ばない
    // （確定は済んでいる。呼び出し側が先に切れても後始末を飛ばさない。以前は初回の保存だけ `RequestAborted` を渡しており、
    // 呼び出し側の時間切れで保存が取り消され、例外が確定の応答を 500 にしていた）。
    // 予算切れは警告を残して続行する（確定は 200 のまま）。置き換えが終わらなければドラフトの写しは消さない（ADR-0068 決定6 の順序）。
    // 残りは次の入れ直し（`POST /reports/knowledge-base/reingest`）が最新の確定版に揃える。
    private static async Task RunKnowledgeAftercareAsync(
        ReportService.Domain.TradingReport r, ConfirmResult result, string actor, string? effectiveBefore, AppSvc svc,
        IKnowledgeBaseWriter kb, IReportDraftKnowledgeCopy? draftCopy, ILoggerFactory loggerFactory, HttpContext http)
    {
        var kbLogger = loggerFactory.CreateLogger("ReportKnowledgeBase");
        var budget = ReportConfirmKnowledgeBudget.Read(http.RequestServices.GetService<IConfiguration>());
        using var budgetCts = new CancellationTokenSource(budget);
        var token = budgetCts.Token;
        try
        {
            // FR-08, IADR-0069/0071 決定3, #565, IADR-0274: 確定報告書を KB へ保存（本文つき。既定 no-op）。
            // 保存の失敗・例外は握りつぶし確定を壊さない（KB は best-effort・保存ポート自体も fail-safe）。
            var savedToKb = false;
            try
            {
                savedToKb = result.SupersededVersion is null
                    ? (await kb.SaveAsync(ReportKnowledgeMapper.ToDocument(r, kbLogger), token).ConfigureAwait(false)).Saved
                    // 🔴 ADR-0068 決定6, IADR-0549 決定5: 再確定は確定版の写しを**最新の確定版で置き換える**（作成すると同じ期間の写しが 2 件になる。
                    // 基盤に外部 ID の upsert が無いので、入れ直しと同じ「一覧で探して本文を差し替える」を 1 つの期間キーだけで行う）。
                    : await ReplaceConfirmedCopyAsync(r, actor, http, kbLogger, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                kbLogger.LogWarning(ex, "確定報告書 {PeriodKey} の KB 保存に失敗しました（確定は継続）。", r.PeriodKey);
            }

            // ADR-0068 決定6, IADR-0549 決定5: 日報の確定で効いている日報が移ったら、移る前の日報の版 n+1 のドラフトの写しを失効として残す（削除しない）。
            if (draftCopy is not null && r.Kind == ReportService.Domain.ReportKind.Daily
                && effectiveBefore is not null && !string.Equals(effectiveBefore, r.PeriodKey, StringComparison.Ordinal)
                && svc.StaleRevisionDraft(effectiveBefore) is { } stale)
                await draftCopy.MarkExpiredAsync(stale.Report, stale.Version, token).ConfigureAwait(false);

            // FR-06, FR-08, UC-03, #1300, IADR-0526 決定 3: 確定版の写しを作れたら、承認待ちの写し（ドラフト）を消す（確定版で置き換える）。
            // 確定版を作れなかったときは消さない —— ドラフトは索引されないので検索には出ず、SC-03 で本文を読める状態が残る。
            // 入れ直し（ReportKnowledgeReingestService）が確定版を作るときに残ったドラフトを消す。best-effort（ポートは例外を投げない）。
            if (savedToKb && draftCopy is not null)
                await draftCopy.RemoveAsync(r.Kind, r.PeriodKey, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            kbLogger.LogWarning(
                "確定報告書 {PeriodKey} の KB の後処理が時間予算（{BudgetSeconds} 秒）内に終わりませんでした（確定は継続）。"
                + "次の入れ直しで最新の確定版に揃います。",
                LogSanitizer.Sanitize(r.PeriodKey), budget.TotalSeconds);
        }
    }

    // FR-08, ADR-0068 決定6, #1373, IADR-0549 決定5: 再確定の後、確定版の写しを最新の確定版で置き換える（入れ直しを 1 つの期間キーだけで、
    // 既存の本文も差し替える指定で走らせる。写しが無ければ作る・残った承認待ちの写しは消す）。入れ直しが別に走っていれば今回は置き換えない
    // （警告。次の入れ直しが最新の確定版で揃える）。要求の取り消しは渡さず、KB の後処理の時間予算だけを渡す（#1385, IADR-0556 決定 1）。
    private static async Task<bool> ReplaceConfirmedCopyAsync(
        ReportService.Domain.TradingReport report, string actor, HttpContext http, ILogger kbLogger, CancellationToken cancellationToken)
    {
        var reingest = http.RequestServices.GetService<ReingestKnowledgeBase.ReportKnowledgeReingestService>();
        if (reingest is null)
            return false;

        var run = await reingest.RunAsync(
            ReingestKnowledgeBase.ReportKnowledgeReingestScope.Single(report.PeriodKey), refreshExisting: true, actor, cancellationToken).ConfigureAwait(false);
        if (run.Result is null)
        {
            kbLogger.LogWarning(
                "再確定した報告書 {PeriodKey} の確定版の写しを置き換えられませんでした（KB の入れ直しが実行中）。次の入れ直しで最新の確定版に揃います。",
                LogSanitizer.Sanitize(report.PeriodKey));
            return false;
        }

        return run.Result.Items.Any(i => i.Outcome is ReingestKnowledgeBase.ReportKnowledgeReingestOutcome.Created
            or ReingestKnowledgeBase.ReportKnowledgeReingestOutcome.BodyRefreshed
            or ReingestKnowledgeBase.ReportKnowledgeReingestOutcome.BodyAttached
            or ReingestKnowledgeBase.ReportKnowledgeReingestOutcome.AlreadyPresent);
    }
}

// 確定の要求（版番号付き冪等）。
// OnBehalfOf（任意・末尾に追加）: 呼び出し元が**代理している利用者**（Keycloak 利用者名）。Discord Bot が載せる。
// 信頼するクライアントのトークン以外では無視される（ConfirmingActorResolver）。
internal sealed record ConfirmReportRequest(int ExpectedVersion, string? OnBehalfOf = null);

// FR-07, FR-13, #1025, IADR-0433 決定 7: 確定の応答（従来の TradingReport の項目＋ Transitioned ＋ Version）。
// NFR, IADR-0420: 受け手（通知サービス）の契約テストが送り手の本物の型として参照するため public。
public sealed record ConfirmReportResponse(
    string PeriodKey,
    ReportService.Domain.ReportKind Kind,
    DateOnly PeriodStart,
    ReportService.Domain.ReportState State,
    string? BasedOn,
    int AssumptionsVersion,
    string PolicySummary,
    string Body,
    IReadOnlyList<ReportService.Domain.ReportInput> UnsuppliedInputs,
    DateTimeOffset? ConfirmedAt,
    bool Transitioned,
    int Version)
{
    public static ConfirmReportResponse From(ConfirmResult result)
    {
        var r = result.Report;
        return new(r.PeriodKey, r.Kind, r.PeriodStart, r.State, r.BasedOn, r.AssumptionsVersion, r.PolicySummary, r.Body,
            r.UnsuppliedInputs, r.ConfirmedAt, result.Transitioned, result.Version);
    }
}
