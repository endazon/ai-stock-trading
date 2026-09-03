using NotificationService.Domain;
using Microsoft.Extensions.Configuration;

namespace NotificationService.Infrastructure.ExternalServices;

// FR-14, IADR-0062: 構成から DiscordBotOptions を読む。
// AllowedUserIds は配列（`Bot:AllowedUserIds:0`）とカンマ区切り文字列の双方を受ける。環境変数で与える場合に
// 配列添字より 1 変数のカンマ区切りの方が扱いやすいため（docker-compose / helm の値と .env の両方に対応する）。
//
// 未設定はすべて既定（＝拒否側）のままにする。ここで「空なら全許可」等の補完は決して行わない。
public static class DiscordBotOptionsReader
{
    public static DiscordBotOptions Read(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var section = config.GetSection(DiscordBotOptions.SectionName);
        var options = new DiscordBotOptions
        {
            Enabled = bool.TryParse(section["Enabled"], out var enabled) && enabled,
            Token = section["Token"],
            GuildId = section["GuildId"],
            ChannelId = section["ChannelId"],
            KillSwitchConfirmationPhrase = section["KillSwitchConfirmationPhrase"],
        };

        foreach (var id in ReadUserIds(section))
            options.AllowedUserIds.Add(id);

        foreach (var (discordUserId, keycloakUser) in ReadUserMapping(section))
            options.UserMapping[discordUserId] = keycloakUser;

        return options;
    }

    // UserMapping も 2 形式を受ける:
    //   1. セクション形式（appsettings / helm）: `UserMapping:<discordUserId>` = <keycloak 利用者名>
    //   2. コンパクト形式（環境変数 / .env）: `UserMapping` = "<discordUserId>:<利用者名>,<discordUserId>:<利用者名>"
    // 環境変数で動的キーを組み立てるのは compose/helm 双方で扱いづらいため、1 変数で与えられる形式を用意する。
    private static IEnumerable<(string DiscordUserId, string KeycloakUser)> ReadUserMapping(IConfiguration section)
    {
        var mapping = section.GetSection("UserMapping");

        var fromSection = mapping.GetChildren()
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .Select(c => (c.Key, c.Value!))
            .ToList();

        if (fromSection.Count > 0)
            return fromSection;

        var raw = mapping.Value;
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split(':', 2, StringSplitOptions.TrimEntries))
            // 形式不正（":" 無し・いずれかが空）は黙って捨てる。**推測で対応付けを作らない**
            // （誤った対応付けは他人の操作を本人として実行させ得るため、拒否側に倒す）。
            .Where(parts => parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            .Select(parts => (parts[0], parts[1]));
    }

    private static IEnumerable<string> ReadUserIds(IConfiguration section)
    {
        // 配列形式（AllowedUserIds:0 / :1 …）を優先する。
        var fromArray = section.GetSection("AllowedUserIds").GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToList();

        if (fromArray.Count > 0)
            return fromArray;

        // カンマ区切り文字列（環境変数向け）。
        var raw = section["AllowedUserIds"];
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
