namespace AiStockTrading.Notification.Application.State;

// FR-14, IADR-0063: Discord Bot の多層認証・有効化の設定。
//
// 安全既定（fail-safe）が本型の中核である: **すべての既定値は「拒否」側**に倒してある。
// - Enabled=false: Gateway に接続しない（既定）
// - GuildId/ChannelId 未設定: 全着信を拒否（空＝全許可にしない）
// - AllowedUserIds 空: 全ユーザーを拒否
// - KillSwitchConfirmationPhrase 空: kill switch 起動を拒否（誤爆防止の閂を設定漏れで外さない）
//
// 設定漏れが「全開放」ではなく「全拒否」になることを DiscordCommandAuthorizer のテストで固定する。
public sealed class DiscordBotOptions
{
    public const string SectionName = "Notifications:Discord:Bot";

    // Gateway 常駐の有効化。false（既定）なら接続しない。
    public bool Enabled { get; set; }

    // Bot トークン（秘匿）。値はコミットしない。空なら有効化されない。
    public string? Token { get; set; }

    // 認証層2: 利用者が所有する専用サーバー。空なら全拒否。
    public string? GuildId { get; set; }

    // 認証層3: 専用チャンネル。空なら全拒否。
    public string? ChannelId { get; set; }

    // 認証層4: 操作を受け付ける Discord ユーザーID（利用者本人のみ）。空なら全拒否。
    public IList<string> AllowedUserIds { get; } = [];

    // 認証層5: Discord ユーザーID → Keycloak 利用者名。操作は対応する利用者の権限・actor で実行する
    // （詳細設計07「Keycloak とのマッピング」）。対応が無いユーザーは actor を特定できないため拒否する。
    public IDictionary<string, string> UserMapping { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    // 認証層6: kill switch 起動時に入力を要求する確認フレーズ。空なら起動を拒否する。
    public string? KillSwitchConfirmationPhrase { get; set; }

    // Gateway 有効化の条件: 明示的に Enabled かつトークンがある。いずれか欠ければ接続しない（安全側）。
    public bool IsGatewayEnabled => Enabled && !string.IsNullOrWhiteSpace(Token);
}
