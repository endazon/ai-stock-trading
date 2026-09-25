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
        if (command.Kind != BotCommandKind.PolicyRevise && IsPolicyCommand(context.RawCommand))
        {
            // `/policy` だが period が書式外（空白・記号・長すぎ）。許可の問題ではないので、形式を案内する。
            logger.LogWarning("方針の改訂の会話キーが書式外のため拒否しました（Actor={Actor}）。", auth.Actor);
            return PolicyRevisionCommandResult.Failed("会話キー（period）の形式が不正です（英数字とハイフンのみ・例: daily-2026-09-28）。");
        }

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

        // 🔴 方針は全文を（必要なら複数の通に分けて）見せる。確認ボタンは最後の通にだけ付く（ADR-0003）。
        var messages = PolicyRevisionMessage.Build(
            proposal.PeriodKey,
            proposal.Version,
            proposal.Presented,
            proposal.Created,
            proposal.PolicySummary,
            [.. proposal.WatchlistChanges.Select(c => (c.Action, c.Symbol, c.Reason))],
            proposal.Rationale);

        logger.LogInformation(
            "方針の改訂案を受け取りました（Actor={Actor}・PeriodKey={PeriodKey}・版={Version}・提示={Presented}）。",
            auth.Actor, proposal.PeriodKey, proposal.Version, proposal.Presented);

        // 確認ボタンは**承認待ちにできた版に限って**出す（未提示の版は確定 API が受け付けない）。
        // 会話キーは報告書サービスが返した値であり、ボタンの CustomId と確定要求へ載る——値域を再確認する。
        var approvable = proposal.Presented && BotCommandParser.IsPeriodKey(proposal.PeriodKey) && proposal.Version >= 1;
        return PolicyRevisionCommandResult.Proposed(messages, approvable ? proposal.PeriodKey : null, approvable ? proposal.Version : null);
    }

    // `/policy` で始まるか（書式外の period を「許可されていない」と読み違えないため）。
    private static bool IsPolicyCommand(string raw)
    {
        var first = raw.TrimStart().Split(' ', 2)[0].ToLowerInvariant();
        return first is "/policy" or "policy";
    }
}

// FR-07, #1016: `/policy` の処理結果。
// WasExecuted=false は案が無い（拒否・検証・報告書サービスの失敗・不明）。IsDenied は多層認証・解析で弾いたこと。
// Messages は順に送る通（案のときは見出し・方針の全文〔分割あり〕・入れ替え案と説明。失敗のときは 1 通）。
// PeriodKey / Version は確認ボタンへ載せる値（承認待ちにできたときだけ非 null）。**ボタンは最後の通にだけ付ける。**
public sealed record PolicyRevisionCommandResult(
    bool WasExecuted,
    IReadOnlyList<string> Messages,
    string? PeriodKey = null,
    int? Version = null,
    bool IsDenied = false)
{
    // 1 通にまとめた表示（ログ・失敗の応答用）。
    public string Message => string.Join("\n\n", Messages);

    public static PolicyRevisionCommandResult Denied(string reason) => new(false, [reason], IsDenied: true);

    public static PolicyRevisionCommandResult Failed(string message) => new(false, [message]);

    public static PolicyRevisionCommandResult Proposed(IReadOnlyList<string> messages, string? periodKey, int? version) =>
        new(true, messages, periodKey, version);
}
