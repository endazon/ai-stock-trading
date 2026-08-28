using NotificationService.Application.Services;
using NotificationService.Application.State;
using AwesomeAssertions;
using Xunit;

namespace NotificationService.Application.Tests;

// FR-14, UC-06: スラッシュコマンドの解析。受け入れ基準8: /killswitch・/killswitch off の解析と未知コマンドの拒否。
//
// 到達可能性の前提: 本 PR の唯一の呼び出し元 DiscordNetBotGateway は、Discord の構造化スラッシュコマンド
// （off は真偽値オプション）から文字列を自前で組み立てて渡すため、利用者が自由文字列でタイプミスを入力する
// 経路は現時点では存在しない。したがって typo 系（"/killswitch of" 等）の検証は**現行経路の防御ではなく**、
// テキスト入力の受け口（自然文リプライ＝#14 相当）が加わったときに解析器が既定で拒否側に倒れることを
// 先に固定しておくためのもの。未知入力を暗黙実行しないという解析器の契約を表す。
public class BotCommandParserTests
{
    [Theory]
    [InlineData("/killswitch")]
    [InlineData("  /killswitch  ")]
    [InlineData("/KillSwitch")]
    [InlineData("killswitch")]
    public void killswitch_は起動として解析される(string raw)
    {
        BotCommandParser.Parse(raw).Kind.Should().Be(BotCommandKind.KillSwitchEngage);
    }

    [Theory]
    [InlineData("/killswitch off")]
    [InlineData("/killswitch OFF")]
    [InlineData("/killswitch   off")]
    public void killswitch_off_は解除として解析される(string raw)
    {
        BotCommandParser.Parse(raw).Kind.Should().Be(BotCommandKind.KillSwitchDisengage);
    }

    // FR-10, ADR-0009: 一時停止/再開/状態照会の解析。確認ステップの有無は呼び出し側（PauseCommandHandler）が担う。
    [Theory]
    [InlineData("/pause", BotCommandKind.Pause)]
    [InlineData("  /Pause  ", BotCommandKind.Pause)]
    [InlineData("pause", BotCommandKind.Pause)]
    [InlineData("/resume", BotCommandKind.Resume)]
    [InlineData("/RESUME", BotCommandKind.Resume)]
    [InlineData("/status", BotCommandKind.Status)]
    [InlineData("status", BotCommandKind.Status)]
    public void pause_resume_status_が解析される(string raw, BotCommandKind expected)
    {
        BotCommandParser.Parse(raw).Kind.Should().Be(expected);
    }

    // 未知・空・typo は Unknown に倒し、呼び出し側で拒否する（暗黙に何かを実行しない）。
    // 特に "/killswitch of"（typo）が起動として解釈されないことが重要（誤爆防止）。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/report approve")]
    [InlineData("/killswitch of")]
    [InlineData("/killswitch on")]
    [InlineData("/killswitch off now")]
    [InlineData("/pause now")]
    [InlineData("/resume please")]
    [InlineData("/status all")]
    [InlineData("/stage")]
    [InlineData("/stage foo")]
    [InlineData("/stage promote")]
    [InlineData("/stage promote x")]
    [InlineData("/stage promote 4")]
    [InlineData("/stage promote -1")]
    [InlineData("/stage demote nine")]
    [InlineData("/stage status now")]
    [InlineData("/stage withdrawal 2")]
    public void 未知のコマンドは_Unknown_になる(string? raw)
    {
        BotCommandParser.Parse(raw).Kind.Should().Be(BotCommandKind.Unknown);
    }

    // FR-20, UC-06, IADR-0081: 段階ゲートの副コマンド解析。status/withdrawal は引数なし。
    [Theory]
    [InlineData("/stage status", BotCommandKind.StageStatus)]
    [InlineData("stage status", BotCommandKind.StageStatus)]
    [InlineData("  /Stage  Status ", BotCommandKind.StageStatus)]
    [InlineData("/stage withdrawal", BotCommandKind.StageWithdrawal)]
    [InlineData("/stage WITHDRAWAL", BotCommandKind.StageWithdrawal)]
    public void stage_status_withdrawal_が解析される(string raw, BotCommandKind expected)
    {
        BotCommandParser.Parse(raw).Kind.Should().Be(expected);
    }

    // FR-20: promote/demote は遷移先（0〜3）を伴い、TargetStage に保持する。
    [Theory]
    [InlineData("/stage promote 1", BotCommandKind.StagePromote, 1)]
    [InlineData("/stage promote 3", BotCommandKind.StagePromote, 3)]
    [InlineData("/stage demote 0", BotCommandKind.StageDemote, 0)]
    [InlineData("stage demote 2", BotCommandKind.StageDemote, 2)]
    public void stage_promote_demote_は遷移先つきで解析される(string raw, BotCommandKind expectedKind, int expectedStage)
    {
        var command = BotCommandParser.Parse(raw);

        command.Kind.Should().Be(expectedKind);
        command.TargetStage.Should().Be(expectedStage);
    }

    // --- FR-07, FR-14, UC-03〜05, #341, IADR-0240: 報告書レビュー ---------------------------------

    [Theory]
    [InlineData("/report show daily-2026-08-28", BotCommandKind.ReportShow, "daily-2026-08-28", null)]
    [InlineData("report show weekly-2026-w35", BotCommandKind.ReportShow, "weekly-2026-w35", null)]
    // 版番号なしの approve は「確認ボタンを出す前段」。実行可否はハンドラが版番号の有無で判断する。
    [InlineData("/report approve daily-2026-08-28", BotCommandKind.ReportApprove, "daily-2026-08-28", null)]
    [InlineData("/report approve daily-2026-08-28 2", BotCommandKind.ReportApprove, "daily-2026-08-28", 2)]
    [InlineData("/report request-changes daily-2026-08-28", BotCommandKind.ReportRequestChanges, "daily-2026-08-28", null)]
    [InlineData("/report request-changes daily-2026-08-28 3", BotCommandKind.ReportRequestChanges, "daily-2026-08-28", 3)]
    [InlineData("  /REPORT  Approve  DAILY-2026-08-28  4 ", BotCommandKind.ReportApprove, "daily-2026-08-28", 4)]
    public void report_は会話キーと版番号つきで解析される(
        string raw, BotCommandKind expectedKind, string expectedPeriodKey, int? expectedVersion)
    {
        var command = BotCommandParser.Parse(raw);

        command.Kind.Should().Be(expectedKind);
        command.PeriodKey.Should().Be(expectedPeriodKey);
        command.Version.Should().Be(expectedVersion);
    }

    [Theory]
    [InlineData("/report")]
    [InlineData("/report show")]
    [InlineData("/report approve")]
    [InlineData("/report unknown-action daily-2026-08-28")]
    // show は表示専用のため版番号を取らない（余分な引数は typo とみなす）。
    [InlineData("/report show daily-2026-08-28 2")]
    [InlineData("/report approve daily-2026-08-28 2 3")]
    // 版番号は 1 以上の整数のみ。
    [InlineData("/report approve daily-2026-08-28 0")]
    [InlineData("/report approve daily-2026-08-28 -1")]
    [InlineData("/report approve daily-2026-08-28 v2")]
    // 🔴 IADR-0240 決定6: periodKey はそのまま URL パスへ載る。英小文字・数字・ハイフン以外は解析しない。
    [InlineData("/report approve ../../secrets 1")]
    [InlineData("/report approve daily_2026 1")]
    [InlineData("/report approve daily/2026 1")]
    [InlineData("/report approve daily%2f2026 1")]
    [InlineData("/report approve daily-2026-08-28?x=1 1")]
    public void 報告書レビューの書式外は_Unknown_になる(string raw)
    {
        var command = BotCommandParser.Parse(raw);

        command.Kind.Should().Be(BotCommandKind.Unknown);
        command.PeriodKey.Should().BeNull();
    }

    [Fact]
    public void 会話キーの長さ上限を超えると解析しない()
    {
        // 境界値: 32 文字まで許容し、33 文字は拒否する。
        var ok = new string('a', 32);
        var tooLong = new string('a', 33);

        BotCommandParser.Parse($"/report show {ok}").Kind.Should().Be(BotCommandKind.ReportShow);
        BotCommandParser.Parse($"/report show {tooLong}").Kind.Should().Be(BotCommandKind.Unknown);
    }
}
