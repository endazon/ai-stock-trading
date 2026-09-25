using Microsoft.Extensions.Logging;
using NotificationService.Domain;

namespace NotificationService.Features.Notifications.RevisePolicy;

// FR-07, FR-14, UC-03〜05, ADR-0003, #1016, IADR-0431: `/policy`（方針の改訂）の処理。
// 多層認証 → コマンド解析 → 指示の検証 → 報告書サービスの順に閂を掛け、いずれかで不成立なら報告書サービスを呼ばない
// （報告書レビュー・kill switch と同型）。
//
// 🔴 **ここでは何も確定しない。** 報告書サービスは案を新しい版として保存・提示するだけであり、確定は既存の確認ボタン
// （`ReportCommandHandler` の版番号付き確定・OnBehalfOf）だけが行う（ADR-0003「方針の確定には利用者との対話を要する」）。
// 🔴 **監視銘柄は変えない。** 案に含まれる入れ替え案は表示するだけである（FR-14）。
//
// 指示の本文は**ログに出さない**（長さだけ）。本文は報告書の改訂記録（本文）に残る。
public sealed class PolicyRevisionCommandHandler(
    IPolicyRevisionController controller,
    DiscordBotOptions options,
    ILogger<PolicyRevisionCommandHandler> logger)
{
    /// <summary>指示の最大長（文字数）。報告書サービスの上限と揃える。</summary>
    public const int MaxInstructionLength = 1000;

    public async Task<PolicyRevisionCommandResult> HandleAsync(
        DiscordCommandContext context, string? instruction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 閂1: 多層認証（DM・サーバー・チャンネル・許可リスト・Keycloak マッピング）。
        var auth = DiscordCommandAuthorizer.Authorize(context, options);
        if (!auth.IsAllowed)
        {
            logger.LogWarning(
                "Discord コマンドを拒否しました（User={UserId}・Channel={ChannelId}・理由={Reason}）。",
                context.UserId, context.ChannelId, auth.Reason);
            return PolicyRevisionCommandResult.Denied(auth.Reason);
        }

        // 閂2: コマンド解析。`/policy` / `/policy <periodKey>` 以外は実行しない。
        var command = BotCommandParser.Parse(context.RawCommand);
        if (command.Kind != BotCommandKind.PolicyRevise)
        {
            logger.LogWarning(
                "方針の改訂として解釈できないコマンドを拒否しました（Actor={Actor}・Kind={Kind}）。", auth.Actor, command.Kind);
            return PolicyRevisionCommandResult.Denied("方針の改訂コマンドではない");
        }

        // 閂3: 指示の検証（空・長すぎる指示は LLM に渡さない＝費用を使わない）。
        var trimmed = instruction?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return PolicyRevisionCommandResult.Failed("指示が空です。改訂したい内容を instruction に書いてください。");
        if (trimmed.Length > MaxInstructionLength)
            return PolicyRevisionCommandResult.Failed($"指示が長すぎます（{MaxInstructionLength} 文字まで）。");

        logger.LogInformation(
            "方針の改訂を要求します（Actor={Actor}・PeriodKey={PeriodKey}・指示の長さ={Length}）。",
            auth.Actor, command.PeriodKey ?? "(当日の日報)", trimmed.Length);

        var outcome = await controller
            .ReviseAsync(command.PeriodKey, trimmed, auth.Actor!, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.Succeeded || outcome.Proposal is not { } proposal)
        {
            logger.LogWarning(
                "方針の改訂案は保存されませんでした（Actor={Actor}・不明={Indeterminate}）。", auth.Actor, outcome.Indeterminate);
            return PolicyRevisionCommandResult.Failed(outcome.Message);
        }

        var text = PolicyRevisionMessage.Format(
            proposal.PeriodKey,
            proposal.Version,
            proposal.Presented,
            proposal.Created,
            proposal.AutoGenerationSkipped,
            proposal.PolicySummary,
            [.. proposal.WatchlistChanges.Select(c => (c.Action, c.Symbol, c.Reason))],
            proposal.Rationale);

        logger.LogInformation(
            "方針の改訂案を受け取りました（Actor={Actor}・PeriodKey={PeriodKey}・版={Version}・提示={Presented}）。",
            auth.Actor, proposal.PeriodKey, proposal.Version, proposal.Presented);

        // 確認ボタンは**承認待ちにできた版に限って**出す（未提示の版は確定 API が受け付けない）。
        // 会話キーは報告書サービスが返した値であり、ボタンの CustomId と確定要求へ載る——値域を再確認する。
        var approvable = proposal.Presented && BotCommandParser.IsPeriodKey(proposal.PeriodKey) && proposal.Version >= 1;
        return PolicyRevisionCommandResult.Proposed(text, approvable ? proposal.PeriodKey : null, approvable ? proposal.Version : null);
    }
}

// FR-07, #1016: `/policy` の処理結果。
// WasExecuted=false は案が無い（拒否・検証・報告書サービスの失敗・不明）。IsDenied は多層認証・解析で弾いたこと。
// PeriodKey / Version は確認ボタンへ載せる値（承認待ちにできたときだけ非 null）。
public sealed record PolicyRevisionCommandResult(
    bool WasExecuted,
    string Message,
    string? PeriodKey = null,
    int? Version = null,
    bool IsDenied = false)
{
    public static PolicyRevisionCommandResult Denied(string reason) => new(false, reason, IsDenied: true);

    public static PolicyRevisionCommandResult Failed(string message) => new(false, message);

    public static PolicyRevisionCommandResult Proposed(string message, string? periodKey, int? version) =>
        new(true, message, periodKey, version);
}
