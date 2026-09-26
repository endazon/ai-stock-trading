using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Logging;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ReportService.Common.Abstractions;
using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-07, FR-14, UC-03〜05, ADR-0003, #1016, IADR-0431 決定 1・4: 利用者の自由文の指示から方針の改訂案を作り、
// **新しい版のドラフトとして保存して提示する（確定はしない）**。確定は既存の版番号付き冪等確定（利用者のみ）が担う。
//
// 「案」の実体は報告書の新しい版である。別の保留状態（有効期限つきの提案）を持たないのは、報告書の版番号が既に
// 「1 期間に 1 つ・古い版は確定できない・確定は 1 度だけ」を保証しているからである（07 §二重実行防止）。
// 次の改訂が来れば版が進み、前の案の確認ボタンは版不一致で確定できなくなる。
//
// 🔴 **何も保存しない場合を明確に分ける（原則 A）。** LLM の失敗・出力の形式違反・対象の不在・確定済みでは、
// ドラフトに一切触れない。保存は案が検証を通ったときだけ行う。
public sealed partial class ReportPolicyRevisionService(
    IReportStore store,
    IClock clock,
    IReportPolicyReviser reviser,
    PolicyRevisionSchedule schedule,
    IPolicyRevisionLedger ledger,
    PolicyRevisionLimit limit,
    ILogger<ReportPolicyRevisionService> logger)
{
    /// <summary>指示の最大長（文字数）。Discord のスラッシュコマンドの上限と揃える。</summary>
    public const int MaxInstructionLength = 1000;

    // 会話キーの値域（Bot の BotCommandParser と同じ。URL・本文へ載るため英数字とハイフンだけ）。
    [GeneratedRegex(@"\A[A-Za-z0-9-]{1,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PeriodKeyPattern();

    public async Task<PolicyRevisionResult> ReviseAsync(
        string? periodKey,
        string? instruction,
        string actor,
        IReadOnlyList<WatchlistSnapshotItem>? currentWatchlist = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        // FR-13, ADR-0042 決定 1, #1025, IADR-0433 決定 1: 案を作った時点の監視銘柄（Bot が照会して運ぶ）。null＝照会できなかった。
        // 形式が崩れた一覧は受け取らない（楽観排他の基準になるため、推測で直さない）。
        if (currentWatchlist is not null && currentWatchlist.Any(w => w is null || !WatchlistSnapshotItem.IsValid(w.Symbol, w.Market)))
            return PolicyRevisionResult.Rejected(
                PolicyRevisionStatus.InvalidWatchlist, "現在の監視銘柄（currentWatchlist）の形式が不正です。");

        // PR #1027 の監査 L2: 上限（200 件）を超える一覧は `/policy` を失敗させず「分からない」（null）へ倒す
        // ——その案の入れ替えは確定しても適用しない（基準を切り詰めて持つと楽観排他が偽の一致を起こす）。
        if (currentWatchlist is not null && currentWatchlist.Count > WatchlistSnapshotItem.MaxCount)
        {
            logger.LogWarning(
                "現在の監視銘柄が {Count} 件で上限 {Max} 件を超えるため、案を作った時点の監視銘柄は「分からない」として扱います。",
                currentWatchlist.Count, WatchlistSnapshotItem.MaxCount);
            currentWatchlist = null;
        }

        var cleanedInstruction = PolicyRevisionProposalParser.CleanText(instruction);
        if (cleanedInstruction.Length == 0)
            return PolicyRevisionResult.Rejected(PolicyRevisionStatus.InvalidInstruction, "指示が空です。改訂したい内容を書いてください。");
        if (cleanedInstruction.Length > MaxInstructionLength)
            return PolicyRevisionResult.Rejected(
                PolicyRevisionStatus.InvalidInstruction, $"指示が長すぎます（{MaxInstructionLength} 文字まで）。");

        var today = DateOnly.FromDateTime(clock.UtcNow.ToOffset(ReportSchedule.JstOffset).DateTime);
        var todaysDailyKey = ReportPeriod.ExpectedKey(ReportKind.Daily, today);
        var key = string.IsNullOrWhiteSpace(periodKey) ? todaysDailyKey : periodKey.Trim();
        if (!PeriodKeyPattern().IsMatch(key))
            return PolicyRevisionResult.Rejected(
                PolicyRevisionStatus.InvalidPeriodKey, "会話キーの形式が不正です（英数字とハイフンのみ・例: daily-2026-09-28）。");

        var target = ResolveTarget(key, todaysDailyKey, today);
        if (target.Rejection is { } rejection)
            return rejection;

        // FR-14, ADR-0042 決定 3, #1024, IADR-0432 決定 1: 1 日の回数上限（JST の暦日）。**LLM を呼ぶ前に数え、
        // 呼ぶ前に 1 行書く**——応答が返らなかった呼び出しも費用が掛かり得るため上限に数える。上限に達したら LLM を呼ばない。
        // 🔴 数えることと書くことは台帳の 1 つの排他区間で行う（TryBegin。同時の要求で上限を超えない）。
        var attempt = new PolicyRevisionAttempt(
            Guid.NewGuid(), clock.UtcNow, today, actor, key,
            WatchlistSnapshotJson: currentWatchlist is null ? null : SerializeSnapshot(currentWatchlist));
        var begin = ledger.TryBegin(attempt, limit.DailyLimit);
        if (!begin.Begun)
        {
            logger.LogWarning(
                "方針の改訂の 1 日の上限に達しています（Actor={Actor}・本日={Used}・上限={Limit}）。LLM を呼びません。",
                LogSanitizer.Sanitize(actor), begin.UsedBefore, limit.DailyLimit);
            return PolicyRevisionResult.Rejected(
                PolicyRevisionStatus.DailyLimitReached,
                $"本日（{today:yyyy-MM-dd}・JST）の /policy は上限の {limit.DailyLimit} 回に達しています（{begin.UsedBefore} 回実行済み）。"
                + "方針は変わっていません。明日（JST）以降に実行してください。", key);
        }

        var attemptId = attempt.Id;
        var attemptNumber = begin.UsedBefore + 1;

        PolicyRevisionOutcome outcome;
        try
        {
            outcome = await reviser.ReviseAsync(
                new PolicyRevisionContext(
                    target.Kind, key, target.CurrentPolicy, target.Parent, cleanedInstruction,
                    currentWatchlist?.Where(w => w.Market == WatchlistSnapshotItem.UnitedStates).Select(w => w.Symbol).ToList()),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            CompleteBestEffort(attemptId, PolicyRevisionAttemptOutcome.AiFailed, null, null);
            throw;
        }

        if (outcome.Proposal is not { } proposal)
        {
            CompleteBestEffort(attemptId, PolicyRevisionAttemptOutcome.AiFailed, null, null);
            logger.LogWarning(
                "方針の改訂案を作れませんでした（Actor={Actor}・PeriodKey={PeriodKey}・理由={Failure}）。何も保存していません。",
                LogSanitizer.Sanitize(actor), key, outcome.Failure);
            return PolicyRevisionResult.Rejected(
                PolicyRevisionStatus.AiFailed, $"AI の案を作れませんでした（{outcome.Message}）。方針は変わっていません。", key);
        }

        // 保存（版 +1。新規は版 1）。並行更新・確定済みは store が例外で拒む（エンドポイントが 409 へ写す）。
        // 🔴 **LLM を待っている間に報告書が更新されたら（自動生成・別の改訂・確定）、ここで版が合わず保存しない**
        // ——読んだ時点の版（ExpectedVersion）で楽観排他を掛ける。古い土台から作った案で新しい版を踏まない。
        var nextVersion = target.ExpectedVersion + 1;
        var body = AppendRevisionRecord(target.Body, nextVersion, actor, clock.UtcNow, cleanedInstruction, proposal);
        var report = target.Base with
        {
            PolicySummary = proposal.PolicySummary,
            Body = body,
            State = ReportState.Draft,
            ConfirmedAt = null,
        };
        int version;
        try
        {
            version = store.UpsertDraft(report, target.ExpectedVersion);
        }
        catch
        {
            CompleteBestEffort(attemptId, PolicyRevisionAttemptOutcome.SaveFailed, null, null);
            throw;
        }

        // 🔴 PR #1026 の監査 1: **保存の後の台帳の書き込みを失敗させて、保存済みのドラフトを 500 にしない。**
        // ドラフトは既に保存されている（確定されるまで取引に効かない）。ここで例外を上へ投げると「200 以外では何も保存しない」
        // 契約が破れ、利用者には保存済みの案が見えない。台帳の失敗は記録して続ける（行は Pending のまま残り、上限には数えられる）。
        CompleteBestEffort(attemptId, PolicyRevisionAttemptOutcome.Proposed, version, SerializeChanges(proposal.WatchlistChanges));

        // 提示（Drafting→PendingApproval）。確定は利用者の確認ボタン（版番号付き）だけが行う（ADR-0003）。
        //
        // 🔴 **保存の後の提示が失敗しても 409 にしない。** 保存と提示の間に別の更新が入ると、提示は版不一致で拒否され
        // （ReviewDecision）、EF ではまれに並行更新の例外にもなる。ここで例外を上へ投げると、エンドポイントの 409
        // （「何も保存していない」側の応答）が**保存済みの案**を「失敗」と伝えてしまう。保存は済んでいるので、
        // 「保存したが承認待ちにできなかった」（Presented=false・確認ボタンを出さない）として返す。
        bool presented;
        try
        {
            var decision = store.ApplyReview(key, new ReviewCommand(ReviewAction.Present, actor, version));
            presented = decision is { Accepted: true } && decision.Review.State == ReviewState.PendingApproval;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "方針の改訂案は保存しましたが、提示に失敗しました（PeriodKey={PeriodKey}・版={Version}）。", key, version);
            presented = false;
        }

        logger.LogInformation(
            "方針の改訂案を保存し提示しました（Actor={Actor}・PeriodKey={PeriodKey}・版={Version}・新規={Created}・提示={Presented}・"
            + "指示の長さ={InstructionLength}・追加案={Additions}・除外案={Removals}）。",
            LogSanitizer.Sanitize(actor), key, version, target.Created, presented, cleanedInstruction.Length,
            proposal.WatchlistChanges.Count(c => c.Action == WatchlistChangeAction.Add),
            proposal.WatchlistChanges.Count(c => c.Action == WatchlistChangeAction.Remove));

        var usage = $"（本日の /policy: {attemptNumber}/{limit.DailyLimit} 回目）";
        return new PolicyRevisionResult(
            PolicyRevisionStatus.Proposed,
            presented
                ? $"方針の改訂案を保存し、承認待ちにしました（確定するまで取引には適用されません）。{usage}"
                : $"方針の改訂案を保存しましたが、承認待ちにできませんでした（/report show で状態を確認してください）。{usage}",
            key, version, target.Created, presented, proposal);
    }

    // 対象の決定。既存の未確定の報告書はその方針を土台に改訂する。無ければ当日（JST）の日報だけを新しく作れる。
    // 🔴 利用者裁定（2026-09-26）: **営業日にまだ自動生成されていない当日の日報は作らない。** 作ると自動生成は
    // 既存の行を踏まない規則（IADR-0115 決定3）でスキップされ、その日の数値入りの日報が失われる。自動生成の後に
    // /policy を実行すれば、生成されたドラフトを改訂できる。休場日（自動生成が無い日）は作ってよい。
    private RevisionTarget ResolveTarget(string key, string todaysDailyKey, DateOnly today)
    {
        var existing = store.Get(key);
        if (existing is not null)
        {
            if (existing.Report.State == ReportState.Confirmed)
                return RevisionTarget.Reject(PolicyRevisionResult.Rejected(
                    PolicyRevisionStatus.AlreadyConfirmed,
                    $"報告書 {key} は確定済みのため改訂できません（確定済みの方針は変えられません）。", key));

            return new RevisionTarget(
                existing.Report, existing.Report.Kind, existing.Report.PolicySummary, ParentOf(existing.Report.BasedOn),
                existing.Report.Body, existing.Version, Created: false, Rejection: null);
        }

        if (!string.Equals(key, todaysDailyKey, StringComparison.Ordinal))
            return RevisionTarget.Reject(PolicyRevisionResult.Rejected(
                PolicyRevisionStatus.NotFound,
                $"報告書 {key} がありません。新しく作れるのは当日（JST）の日報 {todaysDailyKey} だけです。", key));

        if (schedule.AutoDailyEnabled && ReportSchedule.IsBusinessDay(today, schedule.Schedule))
            return RevisionTarget.Reject(PolicyRevisionResult.Rejected(
                PolicyRevisionStatus.AutoDailyPending,
                $"本日（{today:yyyy-MM-dd}・営業日）の日報 {key} はまだ自動生成されていません。いま作ると、数値入りの自動生成の日報が"
                + $"作られなくなるため作りません。自動生成（{schedule.Schedule.DailyAt:HH:mm} JST 以降）の後に /policy を実行すると、"
                + "そのドラフトを改訂します。", key));

        // 新規の当日の日報（休場日、または自動生成が無効な構成）。土台は直近の確定済み日報
        // （方針・上位方針・前提条件の版を引き継ぐ。数値を発明しない）。
        var latest = store.GetLatestConfirmed(ReportKind.Daily);
        if (latest is null)
            return RevisionTarget.Reject(PolicyRevisionResult.Rejected(
                PolicyRevisionStatus.NoBasePolicy,
                "確定済みの日報が無いため、土台の方針がありません（既存のドラフトの会話キーを指定してください）。", key));

        // 確定しても方針に効かない日報は作らない（最新の確定済み日報は開始日の最大で決まる）。
        if (latest.Report.PeriodStart >= today)
            return RevisionTarget.Reject(PolicyRevisionResult.Rejected(
                PolicyRevisionStatus.WouldNotTakeEffect,
                $"より新しい確定済み日報 {latest.Report.PeriodKey} があるため、{key} を確定しても方針に効きません。", key));

        var created = new TradingReport
        {
            PeriodKey = key,
            Kind = ReportKind.Daily,
            PeriodStart = today,
            BasedOn = latest.Report.BasedOn,
            AssumptionsVersion = latest.Report.AssumptionsVersion,
            PolicySummary = latest.Report.PolicySummary,
        };
        return new RevisionTarget(
            created, ReportKind.Daily, latest.Report.PolicySummary, ParentOf(latest.Report.BasedOn),
            Body: string.Empty, ExpectedVersion: 0, Created: true, Rejection: null);
    }

    // 上位方針は確定済みのものだけを渡す（未確定の上位を方針の根拠にしない。散文ドラフトと同じ扱い）。
    private ParentPolicyReference? ParentOf(string? basedOn)
    {
        if (string.IsNullOrWhiteSpace(basedOn))
            return null;

        var parent = store.Get(basedOn);
        return parent is { Report.State: ReportState.Confirmed }
            ? new ParentPolicyReference(basedOn, parent.Report.PolicySummary)
            : null;
    }

    // 本文の末尾へ改訂の記録を追記する（誰が・いつ・何を指示し・AI が何を案として返したか）。確定時に KB へ保存される。
    internal static string AppendRevisionRecord(
        string existingBody, int version, string actor, DateTimeOffset at, string instruction, PolicyRevisionProposal proposal)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(existingBody))
        {
            sb.Append(existingBody.TrimEnd());
            sb.Append("\n\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"## 利用者の指示による方針の改訂（版 {version}）\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"- 指示者: {actor}\n");
        sb.Append(CultureInfo.InvariantCulture, $"- 日時（UTC）: {at.UtcDateTime:yyyy-MM-dd HH:mm:ss}\n");
        sb.Append("- 指示（原文）:\n\n");
        foreach (var line in instruction.Split('\n'))
            sb.Append("> ").Append(line).Append('\n');

        sb.Append("\n### 改訂後の方針（AI の案）\n\n");
        sb.Append(proposal.PolicySummary).Append('\n');

        sb.Append("\n### 監視銘柄の入れ替え案（/policy の確認ボタンで確定したときに適用する。/report approve では適用しない）\n\n");
        if (proposal.WatchlistChanges.Count == 0)
        {
            sb.Append("- なし\n");
        }
        else
        {
            foreach (var change in proposal.WatchlistChanges)
                sb.Append(CultureInfo.InvariantCulture, $"- {ActionLabel(change.Action)} {change.Symbol}（米国）: {change.Reason}\n");
        }

        if (proposal.Rationale is { } rationale)
        {
            sb.Append("\n### AI の説明\n\n");
            sb.Append(rationale).Append('\n');
        }

        return sb.ToString();
    }

    // 案を作った時点の監視銘柄の記録（楽観排他の基準）。
    public static string SerializeSnapshot(IReadOnlyList<WatchlistSnapshotItem> snapshot) =>
        JsonSerializer.Serialize(snapshot.Select(w => new { symbol = w.Symbol, market = w.Market }), LedgerJson);

    // 台帳の完了の書き込み（失敗しても元の結果・例外を上書きしない）。
    private void CompleteBestEffort(Guid attemptId, PolicyRevisionAttemptOutcome outcome, int? version, string? changesJson)
    {
        try
        {
            ledger.Complete(attemptId, outcome, version, changesJson);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "方針の改訂の台帳を閉じられませんでした（試行={AttemptId}・結果={Outcome}）。行は Pending のまま残ります（上限には数えられます）。",
                attemptId, outcome);
        }
    }

    // 台帳の JSON は日本語をそのまま書く（既定のエンコーダは \uXXXX の 6 文字へ逃がし、列の長さと監査の読みやすさを損なう）。
    internal static readonly JsonSerializerOptions LedgerJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // 案の入れ替えの記録（監査）。列挙は名前で書く（序数に結合しない）。
    public static string SerializeChanges(IReadOnlyList<WatchlistChangeSuggestion> changes) =>
        JsonSerializer.Serialize(changes.Select(c => new
        {
            action = c.Action == WatchlistChangeAction.Add ? "add" : "remove",
            symbol = c.Symbol,
            reason = c.Reason,
        }), LedgerJson);

    public static string ActionLabel(WatchlistChangeAction action) => action switch
    {
        WatchlistChangeAction.Add => "追加",
        WatchlistChangeAction.Remove => "除外",
        _ => action.ToString(),
    };

    private sealed record RevisionTarget(
        TradingReport Base,
        ReportKind Kind,
        string CurrentPolicy,
        ParentPolicyReference? Parent,
        string Body,
        int ExpectedVersion,
        bool Created,
        PolicyRevisionResult? Rejection)
    {
        public static RevisionTarget Reject(PolicyRevisionResult rejection) =>
            new(null!, ReportKind.Daily, string.Empty, null, string.Empty, 0, false, rejection);
    }
}

// FR-07, #1016, IADR-0431: 改訂の結果の種別。Proposed 以外は**何も保存していない**。
public enum PolicyRevisionStatus
{
    Proposed,
    InvalidInstruction,
    InvalidPeriodKey,
    NotFound,
    AlreadyConfirmed,
    NoBasePolicy,
    WouldNotTakeEffect,
    AiFailed,

    /// <summary>営業日で当日の日報がまだ自動生成されていない（作ると自動生成を止めるため作らない）。</summary>
    AutoDailyPending,

    /// <summary>FR-14, ADR-0042 決定 3: 本日（JST）の /policy の回数上限に達している（LLM を呼ばない）。</summary>
    DailyLimitReached,

    /// <summary>FR-13, ADR-0042 決定 1, #1025: 現在の監視銘柄の一覧の形式が不正。</summary>
    InvalidWatchlist,
}

// FR-14, ADR-0042 決定 3, #1024, IADR-0432 決定 1: `/policy` の 1 日（JST）の回数上限。構成 `Reports:PolicyRevision:DailyLimit`。
public sealed record PolicyRevisionLimit(int DailyLimit)
{
    public const string ConfigKey = "Reports:PolicyRevision:DailyLimit";

    /// <summary>既定の上限（回/日）。根拠（1 回あたりの費用の見積り）は IADR-0432 決定 1。</summary>
    public const int DefaultDailyLimit = 10;

    /// <summary>構成から読む。空・未設定・不正値・1 未満は既定へ倒す（上限を無効にする値を作らない）。</summary>
    public static PolicyRevisionLimit Read(string? configured) =>
        new(int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 1
            ? parsed
            : DefaultDailyLimit);
}

// FR-07, #1016, IADR-0431 決定 1（2026-09-26 利用者裁定）: 当日の日報を新しく作ってよいかの判定に使う生成境界。
// AutoDailyEnabled=false（自動生成が無効な構成）では、作っても止める自動生成が無いため営業日でも作ってよい。
public sealed record PolicyRevisionSchedule(ReportScheduleOptions Schedule, bool AutoDailyEnabled);

/// <param name="Status">結果の種別。</param>
/// <param name="Message">利用者へ見せる文（コード定数と会話キーだけ）。</param>
/// <param name="PeriodKey">対象の会話キー（解決できたとき）。</param>
/// <param name="Version">保存した版（Proposed のときだけ 1 以上）。</param>
/// <param name="Created">新しく作った報告書か。</param>
/// <param name="Presented">承認待ちにできたか。false なら確認ボタンの対象にならない。</param>
/// <param name="Proposal">案（Proposed のときだけ非 null）。</param>
public sealed record PolicyRevisionResult(
    PolicyRevisionStatus Status,
    string Message,
    string? PeriodKey,
    int Version,
    bool Created,
    bool Presented,
    PolicyRevisionProposal? Proposal)
{
    public static PolicyRevisionResult Rejected(PolicyRevisionStatus status, string message, string? periodKey = null) =>
        new(status, message, periodKey, 0, false, false, null);
}
