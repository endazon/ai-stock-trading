namespace AiStockTrading.Notification.Application.State;

// FR-14, IADR-0062: Discord から着信したコマンドの文脈（Gateway 実装に依存しない素の値）。
// Discord.Net の型を Application へ持ち込まないための境界。DM は GuildId/ChannelId が専用サーバーと
// 一致し得ないが、なりすまし防止のため IsDirectMessage を明示的に受け取り最優先で拒否する。
public sealed record DiscordCommandContext(
    string? GuildId,
    string? ChannelId,
    string? UserId,
    bool IsDirectMessage,
    string RawCommand);

// FR-14: 多層認証の判定結果。拒否時は理由をログに残す（詳細設計07「それ以外の着信は無視しログのみ残す」）。
public enum AuthorizationDecision
{
    Denied,
    Allowed,
}

// FR-14, IADR-0062: 認証結果。許可時のみ Actor（Keycloak 利用者名）が確定する。
// 拒否理由は利用者へ返さずログのみに用いる（存在の探索を助けないため）。
public sealed record AuthorizationResult(AuthorizationDecision Decision, string? Actor, string Reason)
{
    public bool IsAllowed => Decision == AuthorizationDecision.Allowed;

    public static AuthorizationResult Allow(string actor) =>
        new(AuthorizationDecision.Allowed, actor, "許可");

    public static AuthorizationResult Deny(string reason) =>
        new(AuthorizationDecision.Denied, null, reason);
}
