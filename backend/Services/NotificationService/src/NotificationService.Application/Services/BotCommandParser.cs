using AiStockTrading.Notification.Application.State;

namespace AiStockTrading.Notification.Application.Services;

// FR-14, UC-06, IADR-0063: スラッシュコマンドの解析（純関数）。
// 本 PR のスコープは kill switch のみ（詳細設計07 の /report・/status・/pause・/resume は後続。
// /report は #14 交差、/pause・/resume は Risk 側に対応エンドポイントが無い）。
// 未知のコマンドは Unknown に倒し、呼び出し側で拒否する（暗黙に何かを実行しない）。
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

        if (tokens[0] is not ("/killswitch" or "killswitch"))
            return BotCommand.Unknown;

        // 引数なし＝起動、`off`＝解除。それ以外の引数は Unknown（typo で起動させない）。
        return tokens.Length switch
        {
            1 => new BotCommand(BotCommandKind.KillSwitchEngage),
            2 when tokens[1] == "off" => new BotCommand(BotCommandKind.KillSwitchDisengage),
            _ => BotCommand.Unknown,
        };
    }
}
