using System.Text.RegularExpressions;
using AiStockTrading.Shared.Contracts.Trading;

namespace NotificationService.Domain;

// FR-14, UC-06, ADR-0009, IADR-0062/0075/0081: スラッシュコマンドの解析（純関数）。
// 扱うのは kill switch（/killswitch・/killswitch off）・一時停止（/pause・/resume）・稼働状態照会（/status）・
// 段階ゲート（/stage status・/stage promote <n>・/stage demote <n>・/stage withdrawal）・
// GFV 違反による停止の解除（/gfv clear・#464・ADR-0028 決定3）。
// FR-07, UC-03〜05, IADR-0240: 報告書レビュー（/report show・/report approve・/report request-changes）も扱う
// （IADR-0062 決定6 が #14 交差のため保留していた分。#14 側は版番号付き冪等の確定 API を実装済み）。
// FR-10, FR-11, UC-06, ADR-0041 決定 4, #871, IADR-0423: 乖離の取り込み（/drift adopt <symbol> <market>）も扱う
// （台帳の是正であり設定値の変更ではない。数量は取らない）。
// 未知のコマンドは Unknown に倒し、呼び出し側で拒否する（暗黙に何かを実行しない）。
//
// 🔴 **設定値の変更コマンドは、ここに 1 つも生やさない。** FR-14 は「設定値の変更は Discord からは参照のみ」と
// 定め、例外は kill switch と pause/resume **だけ**である。`/config` `/set` `/watchlist` のような語は
// 既定の `_ => BotCommand.Unknown` に落ち、どのハンドラも実行しない（`DiscordSettingsAreReadOnlyTests` が固定）。
public static class BotCommandParser
{
    // FR-20: 運用段階は Stage 0〜3（TradingStage の値域）。範囲外・欠落は Unknown に倒す（暗黙実行しない）。
    private const int MinStage = 0;
    private const int MaxStage = 3;

    // FR-07, IADR-0240 決定6, #835: 報告書の会話キー（例 `daily-2026-07-07` / `weekly-2026-W38`）。
    // **そのまま URL パスへ載る**ため、英数字・ハイフンのみに限定する（パス・トラバーサル／クエリ注入の余地を
    // parser の段階で消す）。書式外は Unknown へ倒す（推測で補正しない）。
    //
    // 🔴 **大文字英字を許す。** 週報の会話キーは ISO 週の `W` が大文字（`weekly-2026-W38`）であり、
    // 英小文字だけに限ると週報を一度も確定できない（#835 で実測。値域制限の目的＝記号の遮断は損なわれない）。
    //
    // 🔴 #837: **アンカーは `\A…\z` で書く。** .NET の `$` は文字列末尾だけでなく**末尾 LF の直前にもマッチする**
    // （`\Z` も同じ）。`^…$` だと `abc\n` が値域を通り、LF を含む会話キーが URL パスへ運ばれる（#836 の監査が実測）。
    private static readonly Regex PeriodKeyPattern =
        new(@"\A[A-Za-z0-9-]{1,32}\z", RegexOptions.CultureInvariant);

    // 版番号は 1 以上（報告書サービスの版番号は 1 起点。0 以下・数値でないものは Unknown へ倒す）。
    private const int MinVersion = 1;

    // FR-10, FR-14, #871, IADR-0423: 取り込みの銘柄コード（例 `7203` / `AAPL` / `BRK.B`）。**確認ボタンの CustomId と
    // リスク管理への要求本文へ載る**ため、英数字・ピリオド・ハイフンの 1〜16 文字に限る（空白・改行・記号を parser の段階で消す）。
    // 大小文字は**変えない**——台帳の銘柄コードと突き合わせる値であり、推測で補正しない（会話キーと同じ規律）。
    // アンカーは `\A…\z`（.NET の `$` は末尾 LF の直前にもマッチする。#837）。
    private static readonly Regex SymbolPattern =
        new(@"\A[A-Za-z0-9.-]{1,16}\z", RegexOptions.CultureInvariant);

    // FR-10, #871: 取り込みの銘柄コードとして受け付けられる値か（Gateway の確認ボタンの復元と共用する）。
    public static bool IsSymbol(string? value) =>
        !string.IsNullOrEmpty(value) && SymbolPattern.IsMatch(value);

    // FR-10, #871, IADR-0423: 市場の語（`japan`/`jp` と `us`/`unitedstates`。大小文字は吸収する）。それ以外は null
    // （Unknown へ倒す。既定の市場へ暗黙に束縛しない＝API が市場の省略を 400 にするのと同じ向き）。
    public static Market? ParseMarket(string? token) => token?.ToLowerInvariant() switch
    {
        "japan" or "jp" => Market.Japan,
        "us" or "unitedstates" => Market.UnitedStates,
        _ => null,
    };

    // FR-10, #871: 市場を解析できる語へ戻す（確認ボタンの CustomId・コマンド文字列の組み立てに使う。ParseMarket と往復する）。
    public static string MarketToken(Market market) => market switch
    {
        Market.Japan => "japan",
        Market.UnitedStates => "us",
        _ => throw new ArgumentOutOfRangeException(nameof(market), market, "未知の市場です。"),
    };

    // FR-07, #834: 会話キーとして受け付けられる値か（入力補完の候補側と共用する）。
    // **受け付けない値を候補に出すと、選んだ結果が `Unknown` へ倒れる**ため、値域の判定を 1 箇所に保つ。
    public static bool IsPeriodKey(string? value) =>
        !string.IsNullOrEmpty(value) && PeriodKeyPattern.IsMatch(value);

    public static BotCommand Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return BotCommand.Unknown;

        // 前後空白と大小文字のみ吸収する。それ以外は厳密に扱う（曖昧一致で誤起動させない）。
        //
        // 🔴 #835: 小文字化するのは**比較に使う側だけ**である。会話キーは大小文字が意味を持ち
        // （`weekly-2026-W38`）、潰すと報告書サービスの自然キーに一致しない。原文トークンも併せて持つ。
        var rawTokens = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (rawTokens.Length == 0)
            return BotCommand.Unknown;

        var tokens = Array.ConvertAll(rawTokens, token => token.ToLowerInvariant());

        return tokens[0] switch
        {
            "/killswitch" or "killswitch" => ParseKillSwitch(tokens),
            // pause/resume/status は引数を取らない（余分な引数は typo とみなし Unknown＝誤起動させない）。
            "/pause" or "pause" when tokens.Length == 1 => new BotCommand(BotCommandKind.Pause),
            "/resume" or "resume" when tokens.Length == 1 => new BotCommand(BotCommandKind.Resume),
            "/status" or "status" when tokens.Length == 1 => new BotCommand(BotCommandKind.Status),
            "/stage" or "stage" => ParseStage(tokens),
            // FR-19, UC-06, #464, ADR-0028: /gfv clear のみ。副コマンドの省略・typo は Unknown へ倒す
            // （暗黙に統制を解除しない）。
            "/gfv" or "gfv" when tokens.Length == 2 && tokens[1] == "clear" =>
                new BotCommand(BotCommandKind.GoodFaithViolationClear),
            // FR-07, FR-14, UC-03〜05, IADR-0240: 報告書レビュー。
            "/report" or "report" => ParseReport(tokens, rawTokens),
            // FR-10, FR-11, UC-06, ADR-0041 決定 4, #871, IADR-0423: 乖離の取り込み。
            "/drift" or "drift" => ParseDrift(tokens, rawTokens),
            _ => BotCommand.Unknown,
        };
    }

    // FR-10, FR-11, UC-06, ADR-0041 決定 4, #871, IADR-0423: 乖離の取り込みの副コマンド。
    //   /drift adopt <symbol> <market>   … 取り込み（数量は取らない。観測が決める）
    // 副コマンドは adopt の 1 つだけ。引数の過不足・書式外の銘柄コード・未知の市場はすべて Unknown へ倒す
    // （**台帳を書き換える操作を曖昧一致で起動させない**）。
    private static BotCommand ParseDrift(string[] tokens, string[] rawTokens)
    {
        if (tokens.Length != 4 || tokens[1] != "adopt")
            return BotCommand.Unknown;

        // 銘柄コードは**原文の大小文字のまま**採る（台帳の値と突き合わせるため）。
        var symbol = rawTokens[2];
        if (!IsSymbol(symbol) || ParseMarket(tokens[3]) is not { } market)
            return BotCommand.Unknown;

        return new BotCommand(BotCommandKind.PositionDriftAdopt, Symbol: symbol, Market: market);
    }

    // FR-07, FR-14, UC-03〜05, IADR-0240: 報告書レビューの副コマンド。
    //   /report show <periodKey>                     … 版番号の照会（表示専用）
    //   /report approve <periodKey>                  … 確認ボタンを出す前段（版番号は未確定）
    //   /report approve <periodKey> <version>        … 確定の実行（版番号付き＝詳細設計07 の必須要件）
    //   /report request-changes <periodKey> [<version>]… 差し戻し（修正指示。版番号を省くとハンドラが照会する）
    // 余分な引数・書式外の periodKey・不正な版番号はすべて Unknown へ倒す（誤起動させない）。
    private static BotCommand ParseReport(string[] tokens, string[] rawTokens)
    {
        if (tokens.Length is < 3 or > 4)
            return BotCommand.Unknown;

        // #835: 会話キーだけは**原文の大小文字のまま**採る（副コマンドの分岐は小文字側で行う）。
        var periodKey = rawTokens[2];
        if (!IsPeriodKey(periodKey))
            return BotCommand.Unknown;

        // 版番号は approve（実行）と request-changes で必須、show では書かせない。
        int? version = null;
        if (tokens.Length == 4)
        {
            if (!int.TryParse(tokens[3], out var parsed) || parsed < MinVersion)
                return BotCommand.Unknown;

            version = parsed;
        }

        return tokens[1] switch
        {
            "show" when version is null => new BotCommand(BotCommandKind.ReportShow, PeriodKey: periodKey),
            // 版番号なし＝確認前、版番号あり＝確定の実行。どちらも同一種別で運び、実行可否はハンドラが版番号で判断する。
            "approve" => new BotCommand(BotCommandKind.ReportApprove, PeriodKey: periodKey, Version: version),
            // 差し戻しは可逆（安全方向）。版番号を省いた要求はハンドラが照会して補う。
            "request-changes" =>
                new BotCommand(BotCommandKind.ReportRequestChanges, PeriodKey: periodKey, Version: version),
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
