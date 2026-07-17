using AiStockTrading.Notification.Application.State;

namespace AiStockTrading.Notification.Application.Services;

// FR-14, UC-06, IADR-0062 決定3: 詳細設計07「認証・認可（本人確認）」の多層を上から順に評価する純関数。
//
// 発注機能を持つシステムの操作窓口であるため、**すべての層は不許可を既定とする**。
// 特に「設定が空＝全許可」にしないことが要（設定漏れを全開放にしない）。層は独立しており、
// 1つでも不成立なら以降を評価せず拒否する。
//
// 層1: DM は無条件で拒否（詳細設計07「DM は不使用」・なりすまし/誤送信防止）
// 層2: 専用サーバー（GuildId）一致。未設定なら拒否
// 層3: 専用チャンネル（ChannelId）一致。未設定なら拒否
// 層4: ユーザーID許可リスト。空なら拒否
// 層5: Keycloak マッピング。対応が無ければ actor を特定できないため拒否
//
// 高リスク操作の確認ステップ（層6）は KillSwitchConfirmation が担う（操作種別ごとに異なるため分離）。
public static class DiscordCommandAuthorizer
{
    public static AuthorizationResult Authorize(DiscordCommandContext context, DiscordBotOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        // 層1: DM は無条件拒否。以降の層（チャンネル一致等）を評価するまでもない。
        if (context.IsDirectMessage)
            return AuthorizationResult.Deny("DM での操作は受け付けない（詳細設計07: DM は不使用）");

        // 層2: 専用サーバー限定。未設定は「どのサーバーでも可」ではなく拒否に倒す。
        if (string.IsNullOrWhiteSpace(options.GuildId))
            return AuthorizationResult.Deny("GuildId が未設定のため全拒否（安全既定）");

        if (!string.Equals(context.GuildId, options.GuildId, StringComparison.Ordinal))
            return AuthorizationResult.Deny("専用サーバー以外からの着信");

        // 層3: 専用チャンネル限定。未設定は拒否。
        if (string.IsNullOrWhiteSpace(options.ChannelId))
            return AuthorizationResult.Deny("ChannelId が未設定のため全拒否（安全既定）");

        if (!string.Equals(context.ChannelId, options.ChannelId, StringComparison.Ordinal))
            return AuthorizationResult.Deny("専用チャンネル以外からの着信");

        // 層4: ユーザーID許可リスト。空なら拒否（空＝全許可にしない）。
        if (options.AllowedUserIds.Count == 0)
            return AuthorizationResult.Deny("AllowedUserIds が空のため全拒否（安全既定）");

        if (string.IsNullOrWhiteSpace(context.UserId))
            return AuthorizationResult.Deny("ユーザーIDを特定できない着信");

        if (!options.AllowedUserIds.Contains(context.UserId, StringComparer.Ordinal))
            return AuthorizationResult.Deny("許可リスト外のユーザー（本人以外は操作できない）");

        // 層5: Keycloak マッピング。操作は対応する利用者の権限で実行し、監査ログにも本人として記録するため、
        // 対応が無ければ actor を特定できず操作させない。
        if (!options.UserMapping.TryGetValue(context.UserId, out var actor) || string.IsNullOrWhiteSpace(actor))
            return AuthorizationResult.Deny("Keycloak 利用者への対応付けが無いため拒否（actor を特定できない）");

        return AuthorizationResult.Allow(actor);
    }
}
