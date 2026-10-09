using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Logging;
using Microsoft.Extensions.Logging;
using ReportService.Common.Abstractions;
using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-06, FR-14, FR-16, UC-03〜05, 計画 ADR-0052 決定 1〜5, #1156, IADR-0491: 所有者の操作（`/report regenerate <periodKey>`）で、
// 入力が未供給のまま作られた**未確定の下書き**を、その期間の入力で作り直す。系が自分で下書きを書き換える経路は作らない（ADR-0052 案 B を採らない）。
//
// 順序（各段で断ったら何も保存しない＝下書きはそのまま）:
//   1. 会話キーの形式 → 対象の存在 → 確定済みでないこと（決定 2）。
//   2. 1 日の上限（`/policy` とは別枠。決定 1）。**入力を引く前に**見る（上限で断る要求に依存先への照会を出さない）。
//   3. 期間の入力を自動生成と同じ供給元・同じ規則で引く。期間がもう現在でなければ「今」しか引けない入力（建玉・運用段階）は取りに行かず
//      未供給として扱う（決定 2）。
//   4. **中核の入力（約定・建玉・手動売買の取り込み）の取得に失敗したら断る**（決定 4）。理由を返し、回数は消費しない。
//      🔴 3 で復元できないために未供給とした入力はここに含めない（含めると期間が過ぎた日報は常に断られる）。
//   5. 上限の台帳に 1 行書く（数えることと書くことを 1 つの排他区間で。LLM を呼ぶ前に数える＝応答の無い呼び出しも数える）。
//   6. 事実と散文の節を組み立てる（散文の LLM 費用は `report-regeneration` へ付け替えて計上。決定 1）。
//   7. 方針（方針の要約と `/policy` の改訂の記録）は保ち（決定 3）、作り直しの記録を本文へ足して版を上げて保存する（決定 5）。
//      読んだ時点の版で楽観排他を掛ける（LLM を待つ間に改訂・確定されたら保存しない）。
//   8. 再提示（承認待ち）。台帳を閉じ、監査へ発行する（決定 5）。確定は従来どおり版番号つきの `/report approve` だけが行う。
//      #1182, IADR-0491 決定 5（2026-10-06 追記）: 承認待ちにできた版は、初版と同じ要約で提示の通知（ReportDraftPresented）を出す
//      （`/report show` は本文を返さない〔IADR-0240 決定 4〕ので、通知が作り直した版の中身を見る唯一の経路である）。
public sealed partial class ReportRegenerationService(
    IReportStore store,
    IClock clock,
    ReportAutoGenerator generator,
    ReportAutoGenerationSettings settings,
    IReportRegenerationLedger ledger,
    ReportRegenerationLimit limit,
    IReportRegenerationAuditPublisher audit,
    IReportDraftPresentedNotifier notifier,
    ILogger<ReportRegenerationService> logger)
{
    /// <summary>作り直しの記録の見出しの先頭（版番号の前まで）。次の作り直しはこの見出しから後ろも保つ。</summary>
    public const string RegenerationRecordHeadingPrefix = "## 報告書の作り直しの記録（版 ";

    // 会話キーの値域（Bot の BotCommandParser・`/policy` と同じ。URL・本文へ載るため英数字とハイフンだけ）。
    [GeneratedRegex(@"\A[A-Za-z0-9-]{1,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PeriodKeyPattern();

    public async Task<ReportRegenerationResult> RegenerateAsync(
        string? periodKey, string actor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var key = periodKey?.Trim() ?? string.Empty;
        if (!PeriodKeyPattern().IsMatch(key))
            return ReportRegenerationResult.Rejected(
                ReportRegenerationStatus.InvalidPeriodKey, "会話キーの形式が不正です（英数字とハイフンのみ・例: daily-2026-10-02）。");

        // 1. 対象（計画 ADR-0052 決定 2: 未確定の下書きに限る）。
        var existing = store.Get(key);
        if (existing is null)
            return ReportRegenerationResult.Rejected(
                ReportRegenerationStatus.NotFound, $"報告書 {key} がありません（作り直せるのは既にある未確定の下書きだけです）。", key);

        if (existing.Report.State == ReportState.Confirmed)
            return ReportRegenerationResult.Rejected(
                ReportRegenerationStatus.AlreadyConfirmed,
                $"報告書 {key} は確定済みのため作り直せません（確定済みの版は変えられません）。", key);

        var period = ReportSchedule.PeriodOf(existing.Report.Kind, existing.Report.PeriodStart, settings.Schedule);
        if (!string.Equals(period.PeriodKey, key, StringComparison.Ordinal))
            return ReportRegenerationResult.Rejected(
                ReportRegenerationStatus.PeriodMismatch,
                $"報告書 {key} の種別・開始日から期間を決められないため作り直せません（期待する会話キー {period.PeriodKey}）。", key);

        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(now.ToOffset(ReportSchedule.JstOffset).DateTime);
        var previousVersion = existing.Version;

        // 2. 1 日の上限（`/policy` とは別枠。ADR-0052 決定 1）。入力を引く前に見る。
        var usedBefore = ledger.CountOn(today);
        if (usedBefore >= limit.DailyLimit)
            return RejectForLimit(key, actor, now, today, previousVersion, usedBefore);

        // 3. 期間の入力（ADR-0052 決定 2）。期間がもう現在でなければ、「今」しか引けない入力は取りに行かない。
        var notRestorable = IsPeriodCurrent(period, now, today)
            ? new HashSet<ReportInput>()
            : Enum.GetValues<ReportInput>().Where(ReportInputs.IsPointInTime).ToHashSet();
        var inputs = await generator.CollectInputsAsync(period, notRestorable, cancellationToken).ConfigureAwait(false);

        // 4. 中核の入力の取得に失敗したら断る（ADR-0052 決定 4。回数は消費しない・下書きはそのまま）。
        var coreFailed = inputs.FetchFailed.Where(ReportInputs.IsCore).ToList();
        if (coreFailed.Count > 0)
        {
            RecordRefusalBestEffort(new ReportRegenerationAttempt(
                Guid.NewGuid(), now, today, actor, key, previousVersion, ReportRegenerationOutcome.RefusedCoreUnsupplied,
                UnsuppliedInputs: ReportInputs.Serialize(inputs.FetchFailed),
                NotRestorableInputs: ReportInputs.Serialize(inputs.NotRestorable)));
            logger.LogWarning(
                "報告書の作り直しを断りました。中核の入力を取得できません（Actor={Actor}・PeriodKey={PeriodKey}・版={Version}・入力={Inputs}）。",
                LogSanitizer.Sanitize(actor), LogSanitizer.Sanitize(key), previousVersion, string.Join(',', coreFailed));
            return ReportRegenerationResult.Rejected(
                ReportRegenerationStatus.CoreInputsUnsupplied,
                $"中核の入力（{string.Join("・", ReportInputs.Labels(coreFailed))}）をいま取得できないため、報告書 {key} を作り直しませんでした。"
                + $"現行の下書き（版 {previousVersion}）はそのままです。回数は消費していません。依存先の回復後にもう一度実行してください。",
                key,
                unsupplied: ReportInputs.Labels(inputs.FetchFailed));
        }

        // 5. 上限の台帳（数えることと書くことを 1 つの排他区間で。同時の要求で上限を超えない）。
        var attempt = new ReportRegenerationAttempt(Guid.NewGuid(), now, today, actor, key, previousVersion);
        var begin = ledger.TryBegin(attempt, limit.DailyLimit);
        if (!begin.Begun)
            return RejectForLimit(key, actor, now, today, previousVersion, begin.UsedBefore);

        var attemptNumber = begin.UsedBefore + 1;

        // 6. 事実と散文の節（散文の費用は `report-regeneration` へ付け替える。用途キー＝モデル割当は自動生成と同じ）。
        //    方針の連鎖（上位・前期）の未供給は方針の節に属するので、前の版の記録をそのまま引き継ぐ（決定 3）。
        var unsupplied = new HashSet<ReportInput>(inputs.Unsupplied);
        foreach (var policyInput in existing.Report.UnsuppliedInputs.Where(i => i is ReportInput.ParentPolicy or ReportInput.PreviousPolicy))
            unsupplied.Add(policyInput);

        var parentSummary = ConfirmedParentSummary(existing.Report.BasedOn);

        ReportDraft draft;
        try
        {
            draft = await generator.DraftFromInputsAsync(
                period, inputs, existing.Report.PolicySummary, existing.Report.AssumptionsVersion, existing.Report.BasedOn,
                ReportPolicyDraft.Substance(parentSummary), unsupplied, LlmPurposes.ReportRegeneration, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            CompleteBestEffort(attempt.Id, ReportRegenerationOutcome.AiFailed, null, null, null);
            throw;
        }

        var unsuppliedInputs = ReportInputs.Parse(ReportInputs.Serialize(unsupplied));
        var nextVersion = previousVersion + 1;

        // 7. 方針の節は保ち、事実と散文を差し替える。作り直しの記録を足す（決定 3・5）。
        var body = ComposeBody(
            draft.Markdown, existing.Report.Body, nextVersion, previousVersion, actor, now, unsuppliedInputs, inputs.NotRestorable);
        var report = existing.Report with
        {
            Body = body,
            UnsuppliedInputs = unsuppliedInputs,
            State = ReportState.Draft,
            ConfirmedAt = null,
        };

        int version;
        try
        {
            version = store.UpsertDraft(report, previousVersion);
        }
        catch
        {
            CompleteBestEffort(attempt.Id, ReportRegenerationOutcome.SaveFailed, null, null, null);
            throw;
        }

        // 🔴 保存の後の台帳・提示・監査の失敗で、保存済みの下書きを失敗と伝えない（`/policy` と同じ規律。IADR-0432 監査 1）。
        CompleteBestEffort(
            attempt.Id, ReportRegenerationOutcome.Regenerated, version,
            ReportInputs.Serialize(unsuppliedInputs), ReportInputs.Serialize(inputs.NotRestorable));

        // 8. 再提示（Drafting → PendingApproval）。確定は利用者の版番号つきの確定だけが行う（ADR-0003）。
        bool presented;
        try
        {
            var decision = store.ApplyReview(key, new ReviewCommand(ReviewAction.Present, actor, version));
            presented = decision is { Accepted: true } && decision.Review.State == ReviewState.PendingApproval;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "報告書を作り直して保存しましたが、提示に失敗しました（PeriodKey={PeriodKey}・版={Version}）。", LogSanitizer.Sanitize(key), version);
            presented = false;
        }

        // FR-06, FR-09, 計画 ADR-0052 決定 3（再提示）・決定 5（要約の警告は作り直した版の記録に従う）, #1182: 再提示の通知。初版（ReportAutoGenerator）と同じ要約（数値はコード集計値・散文はサニタイズ済み・
        // この版の未供給の警告・保った方針の利確の書式の警告）を、新しい版で出す。承認待ちにできなかった版は通知しない（IADR-0116 決定 2）。
        var notified = PresentedNotice.NotPresented;
        if (presented && !notifier.Enabled)
        {
            notified = PresentedNotice.Disabled;
        }
        else if (presented)
        {
            var kind = existing.Report.Kind;
            var label = ReportPeriod.Label(kind, period.PeriodStart);
            var summary = ReportSummary.Build(
                kind, label, draft.Pnl, draft.Narrative, unsuppliedInputs,
                // ADR-0051 フォローアップ 1, #1223: 引けた建玉（期間の時点に復元できれば）で保有中の銘柄ごとにも見る。引けなければ方針全体の判定。
                PolicyTakeProfitCheck.WarningFor(kind, existing.Report.PolicySummary, inputs.Positions)
                    // 計画 ADR-0059 決定 2, #1218, IADR-0519 決定 2: 保った週報の方針に書式どおりの「数値目標:」行が無ければ同じく警告する。
                    ?? WeeklyGoalLineCheck.WarningFor(kind, existing.Report.PolicySummary));
            notified = await NotifyPresentedBestEffortAsync(new PresentedReportNotice(key, kind, label, summary, version))
                .ConfigureAwait(false)
                ? PresentedNotice.Sent
                : PresentedNotice.Failed;
        }

        await PublishAuditBestEffortAsync(new ReportRegenerated(
            key, existing.Report.Kind.ToString(), previousVersion, version, actor,
            [.. unsuppliedInputs.Select(i => i.ToString())],
            [.. inputs.NotRestorable.Select(i => i.ToString())],
            now)).ConfigureAwait(false);

        logger.LogInformation(
            "報告書を作り直しました（Actor={Actor}・PeriodKey={PeriodKey}・版={Previous}→{Version}・提示={Presented}・提示の通知={Notified}・"
            + "なお未供給={Unsupplied}・復元できない入力={NotRestorable}・本日 {Attempt}/{Limit} 回目）。",
            LogSanitizer.Sanitize(actor), LogSanitizer.Sanitize(key), previousVersion, version, presented, notified,
            ReportInputs.Serialize(unsuppliedInputs) ?? "なし", ReportInputs.Serialize(inputs.NotRestorable) ?? "なし",
            attemptNumber, limit.DailyLimit);

        return new ReportRegenerationResult(
            ReportRegenerationStatus.Regenerated,
            SuccessMessage(key, version, presented, notified, attemptNumber, unsuppliedInputs, inputs.NotRestorable),
            key, previousVersion, version, presented,
            ReportInputs.Labels(unsuppliedInputs), ReportInputs.Labels(inputs.NotRestorable));
    }

    // 期間がまだ現在か（＝「今」の値がその期間の値でもあるか）。自動生成がいま対象にしている期間（Due）か、今日（JST）を含む期間。
    // 🔴 自動生成も「生成した時点の」建玉・段階を使う（Due の窓の中なら自動生成をいま走らせても同じ値になる）。
    private bool IsPeriodCurrent(DueReport period, DateTimeOffset now, DateOnly today) =>
        (period.PeriodStart <= today && today <= period.PeriodEnd)
        || ReportSchedule.Due(now, settings.Schedule).Any(d => string.Equals(d.PeriodKey, period.PeriodKey, StringComparison.Ordinal));

    // 上位方針は確定済みのものだけを散文の文脈へ渡す（自動生成・`/policy` と同じ扱い）。
    private string? ConfirmedParentSummary(string? basedOn)
    {
        if (string.IsNullOrWhiteSpace(basedOn))
            return null;

        var parent = store.Get(basedOn);
        return parent is { Report.State: ReportState.Confirmed } ? parent.Report.PolicySummary : null;
    }

    private ReportRegenerationResult RejectForLimit(
        string key, string actor, DateTimeOffset now, DateOnly today, int previousVersion, int used)
    {
        RecordRefusalBestEffort(new ReportRegenerationAttempt(
            Guid.NewGuid(), now, today, actor, key, previousVersion, ReportRegenerationOutcome.LimitReached));
        logger.LogWarning(
            "報告書の作り直しの 1 日の上限に達しています（Actor={Actor}・PeriodKey={PeriodKey}・本日={Used}・上限={Limit}）。入力も LLM も呼びません。",
            LogSanitizer.Sanitize(actor), LogSanitizer.Sanitize(key), used, limit.DailyLimit);
        return ReportRegenerationResult.Rejected(
            ReportRegenerationStatus.DailyLimitReached,
            $"本日（{today:yyyy-MM-dd}・JST）の /report regenerate は上限の {limit.DailyLimit} 回に達しています（{used} 回実行済み。"
            + "/policy とは別の枠です）。下書きは変わっていません。明日（JST）以降に実行してください。",
            key);
    }

    /// <summary>
    /// 作り直した本文。<paramref name="regeneratedMarkdown"/>（この版の入力で組み立てた事実と散文。方針の節は保った方針の要約で描かれている）の後ろへ、
    /// 前の本文の<b>方針の改訂の記録・前の作り直しの記録</b>（最初のどちらかの見出しから後ろ）をそのまま置き、最後にこの作り直しの記録を足す。
    /// </summary>
    public static string ComposeBody(
        string regeneratedMarkdown,
        string previousBody,
        int version,
        int previousVersion,
        string actor,
        DateTimeOffset at,
        IReadOnlyList<ReportInput> unsupplied,
        IReadOnlyList<ReportInput> notRestorable)
    {
        var sb = new StringBuilder();
        sb.Append(regeneratedMarkdown.TrimEnd());

        var preserved = PreservedRecords(previousBody);
        if (preserved.Length > 0)
            sb.Append("\n\n").Append(preserved);

        sb.Append("\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"{RegenerationRecordHeadingPrefix}{version}）\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"- 作り直した利用者: {actor}\n");
        sb.Append(CultureInfo.InvariantCulture, $"- 日時（UTC）: {at.UtcDateTime:yyyy-MM-dd HH:mm:ss}\n");
        sb.Append(CultureInfo.InvariantCulture, $"- 作り直す前の版: {previousVersion}\n");
        sb.Append("- なお未供給だった入力: ")
            .Append(unsupplied.Count == 0 ? "なし" : string.Join("・", ReportInputs.Labels(unsupplied)))
            .Append('\n');
        if (notRestorable.Count > 0)
        {
            sb.Append("- うち、期間の時点に復元できないため未供給として扱った入力: ")
                .Append(string.Join("・", ReportInputs.Labels(notRestorable)))
                .Append("（作り直した時点の値を期間の値として書いていません）\n");
        }

        sb.Append("- 方針（方針の要約と `/policy` の改訂の記録）は作り直していません。事実と散文の節だけを、この版の入力で作り直しました。\n");
        return sb.ToString();
    }

    // 前の本文のうち保つ部分（最初の改訂の記録・作り直しの記録の見出しから後ろ）。無ければ空。
    public static string PreservedRecords(string? previousBody)
    {
        if (string.IsNullOrEmpty(previousBody))
            return string.Empty;

        var start = -1;
        foreach (var heading in new[] { ReportPolicyRevisionService.RevisionRecordHeadingPrefix, RegenerationRecordHeadingPrefix })
        {
            var at = HeadingIndex(previousBody, heading);
            if (at >= 0 && (start < 0 || at < start))
                start = at;
        }

        return start < 0 ? string.Empty : previousBody[start..].TrimEnd();
    }

    // 行頭の見出しだけを見る（本文の途中に同じ文字列が引用されていても切らない）。
    private static int HeadingIndex(string body, string heading)
    {
        if (body.StartsWith(heading, StringComparison.Ordinal))
            return 0;

        var at = body.IndexOf("\n" + heading, StringComparison.Ordinal);
        return at < 0 ? -1 : at + 1;
    }

    private static string SuccessMessage(
        string key, int version, bool presented, PresentedNotice notified, int attemptNumber,
        IReadOnlyList<ReportInput> unsupplied, IReadOnlyList<ReportInput> notRestorable)
    {
        var sb = new StringBuilder();
        sb.Append(presented
            ? $"報告書 {key} を作り直し、版 {version} として承認待ちにしました（確定するまで取引には適用されません）。"
            : $"報告書 {key} を作り直し、版 {version} として保存しましたが、承認待ちにできませんでした（/report show で状態を確認してください）。");
        // #1182: 作り直した版の要約は提示の通知で届く（Bot は本文を取りに行かない。IADR-0240 決定 4）。届かないなら黙らずにそう言う。
        if (notified == PresentedNotice.Sent)
            sb.Append(CultureInfo.InvariantCulture, $"版 {version} の要約は提示の通知（報告書ドラフト（承認待ち））で届きます。");
        else if (notified == PresentedNotice.Failed)
            sb.Append(CultureInfo.InvariantCulture, $"版 {version} の提示の通知（要約）を発行できませんでした。");
        else if (notified == PresentedNotice.Disabled)
            sb.Append(CultureInfo.InvariantCulture, $"提示の通知はこの構成では無効のため、版 {version} の要約は通知で届きません。");
        sb.Append(CultureInfo.InvariantCulture, $"方針は変えていません。確定は /report approve {key} で行ってください。");
        sb.Append(CultureInfo.InvariantCulture, $"（本日の /report regenerate: {attemptNumber} 回目）");
        if (unsupplied.Count > 0)
        {
            sb.Append('\n').Append(ReportSummaryMarkers.UnsuppliedWarningPrefix).Append("（確定の前に本文を確認してください）: ")
                .Append(string.Join("・", ReportInputs.Labels(unsupplied)));
            if (notRestorable.Count > 0)
            {
                sb.Append("（うち ").Append(string.Join("・", ReportInputs.Labels(notRestorable)))
                    .Append(" は期間の時点に復元できないため未供給として扱いました）");
            }
        }

        return sb.ToString();
    }

    private void RecordRefusalBestEffort(ReportRegenerationAttempt attempt)
    {
        try
        {
            ledger.RecordRefusal(attempt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "報告書の作り直しを断った記録を台帳へ書けませんでした（PeriodKey={PeriodKey}・結果={Outcome}）。",
                LogSanitizer.Sanitize(attempt.PeriodKey), attempt.Outcome);
        }
    }

    // 台帳の完了の書き込み（失敗しても元の結果・例外を上書きしない）。
    private void CompleteBestEffort(
        Guid attemptId, ReportRegenerationOutcome outcome, int? version, string? unsupplied, string? notRestorable)
    {
        try
        {
            ledger.Complete(attemptId, outcome, version, unsupplied, notRestorable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "報告書の作り直しの台帳を閉じられませんでした（試行={AttemptId}・結果={Outcome}）。行は Pending のまま残ります（上限には数えられます）。",
                attemptId, outcome);
        }
    }

    // 提示の通知（失敗しても保存・提示済みの下書きを失敗と伝えない。記録して応答へ載せる。初版の ReportAutoGenerator.NotifyAsync と同じく best-effort）。
    // 🔴 保存の後の段なので要求の取り消しを渡さない（取り消しで通知と監査の発行を飛ばさない。監査の発行口も取り消しを取らない）。
    // 取り消しを渡していないので、発行口の内部の TaskCanceledException（送信の時間切れ等）も含めてすべて握る
    // （逃がすと保存・提示済みの作り直しが失敗に見え、監査の発行も飛ぶ）。
    private async Task<bool> NotifyPresentedBestEffortAsync(PresentedReportNotice notice)
    {
        try
        {
            await notifier.NotifyAsync(notice, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "報告書を作り直して承認待ちにしましたが、提示の通知を発行できませんでした（PeriodKey={PeriodKey}・版={Version}）。",
                LogSanitizer.Sanitize(notice.PeriodKey), notice.Version);
            return false;
        }
    }

    // 監査の発行（失敗しても保存済みの下書きを失敗と伝えない。記録して続ける）。
    private async Task PublishAuditBestEffortAsync(ReportRegenerated evt)
    {
        try
        {
            await audit.PublishAsync(evt).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "報告書の作り直しの監査を発行できませんでした（PeriodKey={PeriodKey}・版={Version}・操作者={Actor}）。台帳の行は残っています。",
                LogSanitizer.Sanitize(evt.PeriodKey), evt.Version, LogSanitizer.Sanitize(evt.Actor));
        }
    }
}

// FR-06, 計画 ADR-0052 決定 5, IADR-0491 決定 5: 作り直しの監査の発行口（Wolverine の実装は Infrastructure）。
public interface IReportRegenerationAuditPublisher
{
    Task PublishAsync(ReportRegenerated evt);
}

// #1182: 作り直した版の提示の通知の結果（応答の案内文に使う）。
internal enum PresentedNotice
{
    /// <summary>承認待ちにできなかった（通知しない）。</summary>
    NotPresented,
    Sent,
    Failed,

    /// <summary>通知が構成で無効（<c>NotifyOnDraftPresented=false</c>）。</summary>
    Disabled,
}

// 結果の種別。Regenerated 以外は**下書きを変えていない**。
public enum ReportRegenerationStatus
{
    Regenerated,
    InvalidPeriodKey,
    NotFound,
    AlreadyConfirmed,

    /// <summary>報告書の種別・開始日から期間を決められない（手で作られた行の不整合）。</summary>
    PeriodMismatch,

    /// <summary>計画 ADR-0052 決定 1: 本日（JST）の作り直しの回数上限に達している（入力も LLM も呼ばない）。</summary>
    DailyLimitReached,

    /// <summary>計画 ADR-0052 決定 4: 中核の入力の取得に失敗した（作り直さない・回数は消費しない）。</summary>
    CoreInputsUnsupplied,
}

// FR-06, FR-14, 計画 ADR-0052 決定 1, #1156, IADR-0491 決定 3: `/report regenerate` の 1 日（JST）の回数上限。構成 `Reports:Regeneration:DailyLimit`。
public sealed record ReportRegenerationLimit(int DailyLimit)
{
    public const string ConfigKey = "Reports:Regeneration:DailyLimit";

    /// <summary>既定の上限（回/日）。根拠（1 回あたりの費用の見積り）は IADR-0491 決定 3。</summary>
    public const int DefaultDailyLimit = 5;

    public static readonly ReportRegenerationLimit Default = new(DefaultDailyLimit);

    /// <summary>構成から読む。空・未設定・不正値・1 未満は既定へ倒す（上限を無効にする値を作らない）。</summary>
    public static ReportRegenerationLimit Read(string? configured) =>
        new(int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 1
            ? parsed
            : DefaultDailyLimit);
}

/// <param name="Status">結果の種別。</param>
/// <param name="Message">利用者へ見せる文（コード定数・会話キー・入力の表示名だけ）。</param>
/// <param name="PeriodKey">対象の会話キー（解決できたとき）。</param>
/// <param name="PreviousVersion">作り直す前の版（Regenerated のときだけ 1 以上）。</param>
/// <param name="Version">作り直して保存した版（Regenerated のときだけ 1 以上）。</param>
/// <param name="Presented">承認待ちにできたか。</param>
/// <param name="UnsuppliedInputs">なお未供給だった入力の表示名（断ったときは取得に失敗した入力）。</param>
/// <param name="NotRestorableInputs">期間の時点に復元できないため未供給として扱った入力の表示名。</param>
public sealed record ReportRegenerationResult(
    ReportRegenerationStatus Status,
    string Message,
    string? PeriodKey,
    int PreviousVersion,
    int Version,
    bool Presented,
    IReadOnlyList<string> UnsuppliedInputs,
    IReadOnlyList<string> NotRestorableInputs)
{
    public static ReportRegenerationResult Rejected(
        ReportRegenerationStatus status, string message, string? periodKey = null, IReadOnlyList<string>? unsupplied = null) =>
        new(status, message, periodKey, 0, 0, false, unsupplied ?? [], []);
}
