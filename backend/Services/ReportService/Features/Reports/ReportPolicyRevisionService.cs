using System.Globalization;
using System.Text;
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
    ILogger<ReportPolicyRevisionService> logger)
{
    /// <summary>指示の最大長（文字数）。Discord のスラッシュコマンドの上限と揃える。</summary>
    public const int MaxInstructionLength = 1000;

    // 会話キーの値域（Bot の BotCommandParser と同じ。URL・本文へ載るため英数字とハイフンだけ）。
    [GeneratedRegex(@"\A[A-Za-z0-9-]{1,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PeriodKeyPattern();

    public async Task<PolicyRevisionResult> ReviseAsync(
        string? periodKey, string? instruction, string actor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

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
                PolicyRevisionStatus.InvalidPeriodKey, "会話キーの形式が不正です（例: daily-2026-09-28）。");

        var target = ResolveTarget(key, todaysDailyKey, today);
        if (target.Rejection is { } rejection)
            return rejection;

        var outcome = await reviser.ReviseAsync(
            new PolicyRevisionContext(target.Kind, key, target.CurrentPolicy, target.Parent, cleanedInstruction),
            cancellationToken).ConfigureAwait(false);

        if (outcome.Proposal is not { } proposal)
        {
            logger.LogWarning(
                "方針の改訂案を作れませんでした（Actor={Actor}・PeriodKey={PeriodKey}・理由={Failure}）。何も保存していません。",
                actor, key, outcome.Failure);
            return PolicyRevisionResult.Rejected(
                PolicyRevisionStatus.AiFailed, $"AI の案を作れませんでした（{outcome.Message}）。方針は変わっていません。", key);
        }

        // 保存（版 +1。新規は版 1）。並行更新・確定済みは store が例外で拒む（エンドポイントが 409 へ写す）。
        var nextVersion = target.ExpectedVersion + 1;
        var body = AppendRevisionRecord(target.Body, nextVersion, actor, clock.UtcNow, cleanedInstruction, proposal);
        var report = target.Base with
        {
            PolicySummary = proposal.PolicySummary,
            Body = body,
            State = ReportState.Draft,
            ConfirmedAt = null,
        };
        var version = store.UpsertDraft(report, target.ExpectedVersion);

        // 提示（Drafting→PendingApproval）。確定は利用者の確認ボタン（版番号付き）だけが行う（ADR-0003）。
        var decision = store.ApplyReview(key, new ReviewCommand(ReviewAction.Present, actor, version));
        var presented = decision is { Accepted: true } && decision.Review.State == ReviewState.PendingApproval;

        logger.LogInformation(
            "方針の改訂案を保存し提示しました（Actor={Actor}・PeriodKey={PeriodKey}・版={Version}・新規={Created}・提示={Presented}・"
            + "指示の長さ={InstructionLength}・追加案={Additions}・除外案={Removals}）。",
            actor, key, version, target.Created, presented, cleanedInstruction.Length,
            proposal.WatchlistChanges.Count(c => c.Action == WatchlistChangeAction.Add),
            proposal.WatchlistChanges.Count(c => c.Action == WatchlistChangeAction.Remove));

        return new PolicyRevisionResult(
            PolicyRevisionStatus.Proposed,
            presented
                ? "方針の改訂案を保存し、承認待ちにしました（確定するまで取引には適用されません）。"
                : "方針の改訂案を保存しましたが、承認待ちにできませんでした（/report show で状態を確認してください）。",
            key, version, target.Created, presented, proposal,
            AutoGenerationSkipped: target.Created && target.Kind == ReportKind.Daily);
    }

    // 対象の決定。既存の未確定の報告書はその方針を土台に改訂する。無ければ当日（JST）の日報だけを新しく作れる。
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

        // 新規の当日の日報。土台は直近の確定済み日報（方針・上位方針・前提条件の版を引き継ぐ。数値を発明しない）。
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

        sb.Append("\n### 監視銘柄の入れ替え案（提示のみ。適用は設定画面から）\n\n");
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
}

/// <param name="Status">結果の種別。</param>
/// <param name="Message">利用者へ見せる文（コード定数と会話キーだけ）。</param>
/// <param name="PeriodKey">対象の会話キー（解決できたとき）。</param>
/// <param name="Version">保存した版（Proposed のときだけ 1 以上）。</param>
/// <param name="Created">新しく作った報告書か。</param>
/// <param name="Presented">承認待ちにできたか。false なら確認ボタンの対象にならない。</param>
/// <param name="Proposal">案（Proposed のときだけ非 null）。</param>
/// <param name="AutoGenerationSkipped">当日の日報を新しく作ったため、その日の自動生成が行われないか（既存の行を踏まない規則）。</param>
public sealed record PolicyRevisionResult(
    PolicyRevisionStatus Status,
    string Message,
    string? PeriodKey,
    int Version,
    bool Created,
    bool Presented,
    PolicyRevisionProposal? Proposal,
    bool AutoGenerationSkipped = false)
{
    public static PolicyRevisionResult Rejected(PolicyRevisionStatus status, string message, string? periodKey = null) =>
        new(status, message, periodKey, 0, false, false, null);
}
