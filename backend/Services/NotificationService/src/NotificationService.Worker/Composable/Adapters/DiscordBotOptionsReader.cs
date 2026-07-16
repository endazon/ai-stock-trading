using AiStockTrading.Notification.Application.State;
using Microsoft.Extensions.Configuration;

namespace AiStockTrading.Notification.Worker.Composable.Adapters;

// FR-14, IADR-0063: 構成から DiscordBotOptions を読む。
// AllowedUserIds は配列（`Bot:AllowedUserIds:0`）とカンマ区切り文字列の双方を受ける。環境変数で与える場合に
// 配列添字より 1 変数のカンマ区切りの方が扱いやすいため（docker-compose / helm の値と .env の両方に対応する）。
//
// 未設定はすべて既定（＝拒否側）のままにする。ここで「空なら全許可」等の補完は決して行わない。
internal static class DiscordBotOptionsReader
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

        foreach (var entry in section.GetSection("UserMapping").GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(entry.Value))
                options.UserMapping[entry.Key] = entry.Value;
        }

        return options;
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
