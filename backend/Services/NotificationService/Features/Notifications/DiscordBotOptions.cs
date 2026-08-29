namespace NotificationService.Features.Notifications;

// FR-14, IADR-0062: Discord Bot の多層認証・有効化の設定。
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

    // 注: Gateway を有効化してよいかの判定は DiscordBotGatewayFactory が単独で持つ（本型には置かない）。
    // ここに「有効か」を表すプロパティを重ねると起動可否の判断が二箇所に分かれ、片方だけ条件が変わったときに
    // 「有効に見えるのに接続しない（またはその逆）」という乖離を生むため。
}
