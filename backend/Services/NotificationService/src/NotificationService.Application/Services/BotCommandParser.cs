using AiStockTrading.Notification.Application.State;

namespace AiStockTrading.Notification.Application.Services;

// FR-14, UC-06, ADR-0009, IADR-0062/0075: スラッシュコマンドの解析（純関数）。
// 扱うのは kill switch（/killswitch・/killswitch off）と一時停止（/pause・/resume）・稼働状態照会（/status）。
// /report は #14 交差のため対象外。未知のコマンドは Unknown に倒し、呼び出し側で拒否する（暗黙に何かを実行しない）。
public static class BotCommandParser
{
    public static BotCommand Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return BotCommand.Unknown;

        // 前後空白と大小文字のみ吸収する。それ以外は厳密に扱う（曖昧一致で誤起動させない）。
        var tokens = raw.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return BotCommand.Unknown;

        return tokens[0] switch
        {
            "/killswitch" or "killswitch" => ParseKillSwitch(tokens),
            // pause/resume/status は引数を取らない（余分な引数は typo とみなし Unknown＝誤起動させない）。
            "/pause" or "pause" when tokens.Length == 1 => new BotCommand(BotCommandKind.Pause),
            "/resume" or "resume" when tokens.Length == 1 => new BotCommand(BotCommandKind.Resume),
            "/status" or "status" when tokens.Length == 1 => new BotCommand(BotCommandKind.Status),
            _ => BotCommand.Unknown,
        };
    }

    private static BotCommand ParseKillSwitch(string[] tokens) =>
        // 引数なし＝起動、`off`＝解除。それ以外の引数は Unknown（typo で起動させない）。
        tokens.Length switch
        {
            1 => new BotCommand(BotCommandKind.KillSwitchEngage),
            2 when tokens[1] == "off" => new BotCommand(BotCommandKind.KillSwitchDisengage),
            _ => BotCommand.Unknown,
        };
}
