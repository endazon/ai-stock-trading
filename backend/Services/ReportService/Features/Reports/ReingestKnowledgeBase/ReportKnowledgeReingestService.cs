using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Logging;
using AiStockTrading.Shared.Contracts.Operations;
using AiStockTrading.Shared.KnowledgeBase;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using ReportService.Common.Abstractions;
using ReportService.Domain;
using ReportService.Infrastructure.ExternalServices;
using Wolverine;

namespace ReportService.Features.Reports.ReingestKnowledgeBase;

// FR-08, FR-11, #1028, IADR-0436: 確定済みの報告書を KB へ入れ直す（基盤の切替で消えた写しの復旧・本文なしで入った写しの修復）。
//
// 🔴 **基盤には外部 ID での照会・upsert が無い**（MSP DocumentService を読んだ結果。IADR-0436 決定 2）。冪等は AST 側で作る:
//   1. KB の文書一覧を**先に 1 回だけ**引く。引けなければ 1 件も書かない（「無い」と読んで作ると重複を作る）。
//   2. 報告書ごとに、一覧から属性（project=ai-stock-trading・periodKey・kind）の一致する文書を探す。
//      無ければ作る／本文が無ければ本文を入れる（`PUT …/body`＝文書は増えない）／本文があれば何もしない。
//   3. 結果（誰が・範囲・件数・内訳）を ReportKnowledgeReingested で監査台帳へ残す。
// 結果が分からない書き込み（タイムアウト等）は Unknown と書き、成功とも失敗とも数えない。次の実行は一覧で見つけるので重複しない。
//
// 同時に 1 本だけ（ReportKnowledgeReingestGate）。確定時の保存（ConfirmReport）とは独立（そちらは変えない）。
public sealed class ReportKnowledgeReingestService(
    IReportStore store,
    IKnowledgeDocumentCatalog catalog,
    IMessageBus bus,
    IClock clock,
    ReportKnowledgeReingestGate gate,
    ILogger<ReportKnowledgeReingestService> logger)
{
    public const string StatusCompleted = "Completed";
    public const string StatusAborted = "Aborted";
    public const string StatusCancelled = "Cancelled";

    // 監査の内訳（送らなかった・失敗・不明の行）の上限。監査台帳は 7 年消せないため上限を置き、超過は件数で残す。
    public const int MaxAuditBreakdown = 200;

    internal const string EmptyBodyReason = "本文が空です（手動確定など自動生成を経ていない報告書）。KB へは送りません。";

    // 一致する写しがすべて別の主体の所有（基盤が本文の投入を 404 で拒否した）。#665 より前の保存は owner を持たないか
    // owner=system のことがある。AST の資格では消せず、作れば重複になる。
    internal const string NotOwnedReason =
        "KB の写しは別の主体が所有しているため本文を入れられません（AST の KB 用クライアントの所有ではない旧い写しの可能性）。"
        + "重複を作らないため新しい写しは作りません。基盤の管理者が写しを削除してから入れ直してください。";

    internal static readonly string BodyTooLargeReason =
        $"本文が上限（{KnowledgeBodyLimits.MaxBytes} バイト・UTF-8）を超えるため送りません（基盤が拒否します）。";

    public async Task<ReportKnowledgeReingestRun> RunAsync(
        ReportKnowledgeReingestScope scope, bool refreshExisting, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (!gate.TryEnter())
            return ReportKnowledgeReingestRun.Busy;

        try
        {
            return await RunExclusiveAsync(scope, refreshExisting, actor, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Exit();
        }
    }

    private async Task<ReportKnowledgeReingestRun> RunExclusiveAsync(
        ReportKnowledgeReingestScope scope, bool refreshExisting, string actor, CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid();
        var targets = store.List()
            .Where(r => r.State == ReportState.Confirmed && scope.Includes(r))
            .OrderBy(r => r.PeriodStart)
            .ThenBy(r => r.PeriodKey, StringComparer.Ordinal)
            .ToList();

        var listing = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);
        if (listing.Outcome != KnowledgeCatalogOutcome.Succeeded)
        {
            // 🔴 一覧を引けないまま作成へ進まない。既存の写しが見えないので、作れば重複になり得る。
            var reason = listing.Reason ?? "KB の文書一覧を読めませんでした。";
            logger.LogWarning("KB への入れ直しを中止しました（1 件も書いていません）: {Reason}", reason);
            var aborted = Summarize(runId, scope, refreshExisting, StatusAborted, reason, targets.Count,
                [.. targets.Select(r => new ReportKnowledgeReingestItem(
                    r.PeriodKey, r.Kind, ReportKnowledgeReingestOutcome.NotAttempted, null, null))]);
            var abortedResult = aborted with { AuditPublished = await PublishAuditAsync(aborted, actor).ConfigureAwait(false) };
            var httpStatus = listing.Outcome == KnowledgeCatalogOutcome.NotConfigured
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status502BadGateway;
            return new ReportKnowledgeReingestRun(abortedResult, httpStatus);
        }

        var index = listing.Entries
            .Where(e => e.Attributes.ContainsKey(ReportKnowledgeMapper.PeriodKeyAttribute)
                && e.Attributes.ContainsKey(ReportKnowledgeMapper.KindAttribute))
            .GroupBy(e => (e.Attributes[ReportKnowledgeMapper.PeriodKeyAttribute], e.Attributes[ReportKnowledgeMapper.KindAttribute]))
            .ToDictionary(g => g.Key, g => g.ToList());

        var items = new List<ReportKnowledgeReingestItem>(targets.Count);
        var status = StatusCompleted;

        foreach (var report in targets)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                status = StatusCancelled;
                items.Add(new ReportKnowledgeReingestItem(report.PeriodKey, report.Kind, ReportKnowledgeReingestOutcome.NotAttempted, null, null));
                continue;
            }

            index.TryGetValue((report.PeriodKey, report.Kind.ToString()), out var sameKey);
            var matches = sameKey?.Where(e => IsCopyOf(e, report)).ToList() ?? [];

            try
            {
                var item = await ReingestOneAsync(report, matches, refreshExisting, cancellationToken).ConfigureAwait(false);
                items.Add(item with { MatchedCopies = matches.Count });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 呼び出し元が切れた。送った要求の結果は分からない。以降は試さない。
                status = StatusCancelled;
                items.Add(new ReportKnowledgeReingestItem(report.PeriodKey, report.Kind, ReportKnowledgeReingestOutcome.Unknown, null,
                    "呼び出しが打ち切られました（この報告書の送信の結果は分かりません）。", matches.Count));
            }
        }

        var summary = Summarize(runId, scope, refreshExisting, status, null, targets.Count, items);
        var result = summary with { AuditPublished = await PublishAuditAsync(summary, actor).ConfigureAwait(false) };
        return new ReportKnowledgeReingestRun(result, StatusCodes.Status200OK);
    }

    // FR-08, #1028, IADR-0436 決定 2［2026-09-26 PR #1038 の監査で改めた］: KB の文書がこの報告書の写しか。
    // periodKey・kind の一致（索引で絞り済み）に加えて、次のどちらか:
    //   - `project=ai-stock-trading` を持つ（#665・2026-09-03 以降の保存）。
    //   - `project` を持たず、表題が確定時の写像の表題（ReportKnowledgeMapper.TitleOf）と完全に一致する（#665 より前の保存。
    //     #665 より前の本文なしの写しはこちら。以降の手動確定の本文なしの写しは project を持つ）。🔴 これを外すと旧い写しの隣に 2 つ目を作る。
    // 別のプロジェクトの値を持つ文書は写しに数えない。
    internal static bool IsCopyOf(KnowledgeCatalogEntry entry, TradingReport report)
    {
        if (entry.Attributes.TryGetValue(KnowledgeAttributeDefaults.ProjectKey, out var project) && !string.IsNullOrEmpty(project))
            return string.Equals(project, KnowledgeAttributeDefaults.RequiredProject, StringComparison.Ordinal);

        return string.Equals(entry.Title, ReportKnowledgeMapper.TitleOf(report.Kind, report.PeriodKey), StringComparison.Ordinal);
    }

    private async Task<ReportKnowledgeReingestItem> ReingestOneAsync(
        TradingReport report, List<KnowledgeCatalogEntry> matches, bool refreshExisting, CancellationToken cancellationToken)
    {
        // #565 と同じ扱い: 空の本文は送らない（本文なしの写しを作らない・既存の写しを空で上書きしない）。
        if (string.IsNullOrEmpty(report.Body))
            return Item(report, ReportKnowledgeReingestOutcome.SkippedEmptyBody, null, EmptyBodyReason);

        // 基盤は 1 MB 超を 413 で拒否する。保存ポートのように本文を外してメタデータだけで作ると、
        // 検索できない写しができて次の実行からは「在る」に見える——作らない。
        if (KnowledgeBodyLimits.Exceeds(report.Body))
            return Item(report, ReportKnowledgeReingestOutcome.SkippedBodyTooLarge, null, BodyTooLargeReason);

        // 🔴 作るのは一致する写しが 1 件も無いときだけ。一致する写しがあれば、書けなくても作らない（重複させない）。
        if (matches.Count == 0)
        {
            var created = await catalog.CreateAsync(ReportKnowledgeMapper.ToDocument(report), cancellationToken).ConfigureAwait(false);
            return FromWrite(report, created, ReportKnowledgeReingestOutcome.Created, null);
        }

        // 並び: 本文のある写し → project を持つ写し（AST の KB 用クライアントが所有者として作った形）→ 更新の新しい写し。
        var ordered = matches
            .OrderByDescending(m => m.HasStoredBody)
            .ThenByDescending(m => m.Attributes.ContainsKey(KnowledgeAttributeDefaults.ProjectKey))
            .ThenByDescending(m => m.UpdatedAt)
            .ToList();
        var first = ordered[0];

        if (first.HasStoredBody && !refreshExisting)
            return Item(report, ReportKnowledgeReingestOutcome.AlreadyPresent, first.DocumentId, null);

        // 本文を入れる相手は、同じ段（本文あり／なし）の写しを順に試す。基盤は所有者でない写しへの投入を 404 で拒否するので、
        // 404 なら次の写しへ進み（AST が所有する写しを採る）、それ以外の失敗・不明はそこで止める。
        // 🔴 不明（タイムアウト・5xx）で次の写しへ進まない —— 最初の写しに入ったかもしれず、2 つの写しへ書くことになる。
        // 🔴 段を跨がない —— `refreshExisting` で本文ありの写しがすべて 404 でも、本文なしの写しへは入れない（入れ直しの指定は
        //    「本文のある写しの索引を作り直す」であり、別の写しを検索に出す操作ではない。本文ありの写しが別の主体の所有なら、
        //    管理者がそれを削除すれば、次の実行が本文なしの写しへ入れる）。T-10-1501 が固定する。
        var success = first.HasStoredBody ? ReportKnowledgeReingestOutcome.BodyRefreshed : ReportKnowledgeReingestOutcome.BodyAttached;
        foreach (var candidate in ordered.Where(m => m.HasStoredBody == first.HasStoredBody))
        {
            var put = await catalog.PutBodyAsync(candidate.DocumentId, report.Body, cancellationToken).ConfigureAwait(false);
            if (!put.IsNotFoundOrNotOwner)
                return FromWrite(report, put, success, candidate.DocumentId);
        }

        return Item(report, ReportKnowledgeReingestOutcome.Failed, first.DocumentId, NotOwnedReason);
    }

    private static ReportKnowledgeReingestItem FromWrite(
        TradingReport report, KnowledgeCatalogWriteResult write, ReportKnowledgeReingestOutcome success, Guid? knownDocumentId) =>
        write.Outcome switch
        {
            KnowledgeCatalogOutcome.Succeeded => Item(report, success, write.DocumentId ?? knownDocumentId, null),
            KnowledgeCatalogOutcome.Unknown => Item(report, ReportKnowledgeReingestOutcome.Unknown, knownDocumentId, write.Reason),
            // NotConfigured は一覧の段で中止しているので来ない。来たら書けていない＝失敗。
            _ => Item(report, ReportKnowledgeReingestOutcome.Failed, knownDocumentId, write.Reason),
        };

    private static ReportKnowledgeReingestItem Item(
        TradingReport report, ReportKnowledgeReingestOutcome outcome, Guid? documentId, string? reason) =>
        new(report.PeriodKey, report.Kind, outcome, documentId, reason);


    private static ReportKnowledgeReingestResult Summarize(
        Guid runId, ReportKnowledgeReingestScope scope, bool refreshExisting, string status, string? abortReason,
        int targeted, IReadOnlyList<ReportKnowledgeReingestItem> items)
    {
        int Count(ReportKnowledgeReingestOutcome o) => items.Count(i => i.Outcome == o);
        var created = Count(ReportKnowledgeReingestOutcome.Created);
        var attached = Count(ReportKnowledgeReingestOutcome.BodyAttached);
        var refreshed = Count(ReportKnowledgeReingestOutcome.BodyRefreshed);
        var emptyBody = Count(ReportKnowledgeReingestOutcome.SkippedEmptyBody);
        var tooLarge = Count(ReportKnowledgeReingestOutcome.SkippedBodyTooLarge);

        return new ReportKnowledgeReingestResult(
            runId, status, abortReason, scope.Describe(), refreshExisting, targeted,
            Sent: created + attached + refreshed,
            Created: created,
            BodyAttached: attached,
            BodyRefreshed: refreshed,
            AlreadyPresent: Count(ReportKnowledgeReingestOutcome.AlreadyPresent),
            Skipped: emptyBody + tooLarge,
            SkippedEmptyBody: emptyBody,
            SkippedBodyTooLarge: tooLarge,
            Failed: Count(ReportKnowledgeReingestOutcome.Failed),
            Unknown: Count(ReportKnowledgeReingestOutcome.Unknown),
            NotAttempted: Count(ReportKnowledgeReingestOutcome.NotAttempted),
            DuplicatesInKb: items.Count(i => i.MatchedCopies > 1),
            Items: items,
            AuditPublished: false);
    }

    // FR-11: 実行の結果を監査台帳へ（誰が・範囲・件数・内訳）。発行の失敗は実行を巻き戻さないが、応答で分かるようにする。
    private async Task<bool> PublishAuditAsync(ReportKnowledgeReingestResult result, string actor)
    {
        var breakdown = result.Items
            .Where(i => i.Outcome is ReportKnowledgeReingestOutcome.SkippedEmptyBody
                or ReportKnowledgeReingestOutcome.SkippedBodyTooLarge
                or ReportKnowledgeReingestOutcome.Failed
                or ReportKnowledgeReingestOutcome.Unknown)
            .Select(i => new ReportKnowledgeReingestEntry(i.PeriodKey, i.Outcome.ToString(), i.Reason, i.DocumentId))
            .ToList();

        var evt = new ReportKnowledgeReingested(
            result.RunId, actor, result.Scope, result.RefreshExisting, result.Status, result.AbortReason,
            result.Targeted, result.Created, result.BodyAttached, result.BodyRefreshed, result.AlreadyPresent,
            result.SkippedEmptyBody, result.SkippedBodyTooLarge, result.Failed, result.Unknown, result.NotAttempted,
            result.DuplicatesInKb,
            [.. result.Items.Where(i => i.MatchedCopies > 1).Select(i => i.PeriodKey).Take(MaxAuditBreakdown)],
            [.. breakdown.Take(MaxAuditBreakdown)],
            Math.Max(0, breakdown.Count - MaxAuditBreakdown),
            clock.UtcNow);

        try
        {
            await bus.PublishAsync(evt).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "KB への入れ直しの監査の発行に失敗しました（RunId={RunId}・操作者={Actor}・送信 {Sent} 件・失敗 {Failed} 件・不明 {Unknown} 件）。",
                result.RunId, LogSanitizer.Sanitize(actor), result.Sent, result.Failed, result.Unknown);
            return false;
        }
    }
}

// FR-08, #1028: 実行の結果と HTTP の状態（200＝実行した／503・502＝中止／409＝実行中）。Busy のとき Result は null。
public sealed record ReportKnowledgeReingestRun(ReportKnowledgeReingestResult? Result, int StatusCode)
{
    public static readonly ReportKnowledgeReingestRun Busy = new(null, StatusCodes.Status409Conflict);
}
