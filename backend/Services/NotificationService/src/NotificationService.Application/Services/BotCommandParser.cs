using AiStockTrading.Notification.Application.State;

namespace AiStockTrading.Notification.Application.Services;

// FR-14, UC-06, ADR-0009, IADR-0062/0075/0081: スラッシュコマンドの解析（純関数）。
// 扱うのは kill switch（/killswitch・/killswitch off）・一時停止（/pause・/resume）・稼働状態照会（/status）・
// 段階ゲート（/stage status・/stage promote <n>・/stage demote <n>・/stage withdrawal）。
// /report は #14 交差のため対象外。未知のコマンドは Unknown に倒し、呼び出し側で拒否する（暗黙に何かを実行しない）。
public static class BotCommandParser
{
    // FR-20: 運用段階は Stage 0〜3（TradingStage の値域）。範囲外・欠落は Unknown に倒す（暗黙実行しない）。
    private const int MinStage = 0;
    private const int MaxStage = 3;

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
            "/stage" or "stage" => ParseStage(tokens),
            _ => BotCommand.Unknown,
        };
    }

    // FR-20, UC-06: 段階ゲートの副コマンド。status/withdrawal は引数なし、promote/demote は遷移先（0〜3）を取る。
    // 余分な引数・範囲外・数値でない遷移先は Unknown に倒す（誤起動させない）。
    private static BotCommand ParseStage(string[] tokens)
    {
        if (tokens.Length == 2)
        {
            return tokens[1] switch
            {
                "status" => new BotCommand(BotCommandKind.StageStatus),
                "withdrawal" => new BotCommand(BotCommandKind.StageWithdrawal),
                _ => BotCommand.Unknown,
            };
        }

        if (tokens.Length == 3 && TryParseStage(tokens[2], out var target))
        {
            return tokens[1] switch
            {
                "promote" => new BotCommand(BotCommandKind.StagePromote, target),
                "demote" => new BotCommand(BotCommandKind.StageDemote, target),
                _ => BotCommand.Unknown,
            };
        }

        return BotCommand.Unknown;
    }

    // 遷移先は Stage 0〜3 のみ許容する。範囲外は false（Unknown へ倒し、Risk に不正値を投げない）。
    private static bool TryParseStage(string token, out int stage) =>
        int.TryParse(token, out stage) && stage is >= MinStage and <= MaxStage;

    private static BotCommand ParseKillSwitch(string[] tokens) =>
        // 引数なし＝起動、`off`＝解除。それ以外の引数は Unknown（typo で起動させない）。
        tokens.Length switch
        {
            1 => new BotCommand(BotCommandKind.KillSwitchEngage),
            2 when tokens[1] == "off" => new BotCommand(BotCommandKind.KillSwitchDisengage),
            _ => BotCommand.Unknown,
        };
}
