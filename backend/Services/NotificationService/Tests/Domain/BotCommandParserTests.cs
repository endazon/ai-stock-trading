using NotificationService.Domain;
using AwesomeAssertions;
using Xunit;

namespace NotificationService.Tests;

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
    // 🔴 #835: 週報の会話キーは ISO 週の W が大文字（`weekly-2026-W38`）。**大小文字を潰さない**。
    // 潰すと報告書サービスの自然キーに一致せず、週報を一度も確定できない（稼働環境で実測）。
    [InlineData("/report show weekly-2026-W38", BotCommandKind.ReportShow, "weekly-2026-W38", null)]
    [InlineData("report show weekly-2026-W35", BotCommandKind.ReportShow, "weekly-2026-W35", null)]
    [InlineData("/report approve weekly-2026-W38 3", BotCommandKind.ReportApprove, "weekly-2026-W38", 3)]
    [InlineData(
        "/report request-changes weekly-2026-W38 3", BotCommandKind.ReportRequestChanges, "weekly-2026-W38", 3)]
    // 版番号なしの approve は「確認ボタンを出す前段」。実行可否はハンドラが版番号の有無で判断する。
    [InlineData("/report approve daily-2026-08-28", BotCommandKind.ReportApprove, "daily-2026-08-28", null)]
    [InlineData("/report approve daily-2026-08-28 2", BotCommandKind.ReportApprove, "daily-2026-08-28", 2)]
    [InlineData("/report request-changes daily-2026-08-28", BotCommandKind.ReportRequestChanges, "daily-2026-08-28", null)]
    [InlineData("/report request-changes daily-2026-08-28 3", BotCommandKind.ReportRequestChanges, "daily-2026-08-28", 3)]
    // 動詞・副コマンドの大小文字は従来どおり吸収する（会話キーだけが原文のまま渡る。#835）。
    [InlineData("  /REPORT  Approve  daily-2026-08-28  4 ", BotCommandKind.ReportApprove, "daily-2026-08-28", 4)]
    [InlineData("  /REPORT  Show  weekly-2026-W38 ", BotCommandKind.ReportShow, "weekly-2026-W38", null)]
    // 会話キーは照合せず**そのまま**渡す（推測で補正しない）。実在しないキーは報告書サービスが 404 で返す。
    [InlineData("/report show DAILY-2026-08-28", BotCommandKind.ReportShow, "DAILY-2026-08-28", null)]
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
    // 🔴 IADR-0240 決定6, #835: periodKey はそのまま URL パスへ載る。英数字・ハイフン以外は解析しない
    // （大文字英字は週報キーのため許すが、記号は従来どおり一切許さない）。
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

    // --- FR-14, #837, IADR-0240 決定6: 値域のアンカーは \A…\z（.NET の `$` は末尾 LF の直前にもマッチする）---

    [Theory]
    [InlineData("/report approve daily-2026-08-28\n 1")]
    [InlineData("/report approve abc\n 1")]
    [InlineData("/report approve weekly-2026-W38\n 3")]
    [InlineData("/report request-changes daily-2026-08-28\n 2")]
    // 以下は `^…$` でも通らなかった形。値域の境界として併せて固定する（LF 2 つ・CRLF・先頭 LF・途中 LF）。
    [InlineData("/report approve daily-2026-08-28\n\n 1")]
    [InlineData("/report approve daily-2026-08-28\r\n 1")]
    [InlineData("/report approve \ndaily-2026-08-28 1")]
    [InlineData("/report approve daily-\n2026-08-28 1")]
    public void 会話キーに改行を含む入力は_Unknown_になる(string raw)
    {
        // 否定形: 入力の途中にある LF は、半角空白で割ったトークンに残る。`^…$` だと「末尾 LF の直前」にマッチして
        // `abc\n` が値域を通り、LF を含む会話キーが URL パスへ運ばれていた（#836 の監査が実測）。
        var command = BotCommandParser.Parse(raw);

        command.Kind.Should().Be(BotCommandKind.Unknown);
        command.PeriodKey.Should().BeNull();
    }

    [Theory]
    [InlineData("daily-2026-08-28\n")]
    [InlineData("abc\n")]
    [InlineData("\n")]
    [InlineData("daily-2026-08-28\r\n")]
    public void 末尾に改行を含む値は会話キーとして受け付けない(string value)
    {
        // 入力補完の候補側（ReportPeriodSuggestions）と共用する判定。パーサと同じ 1 箇所で値域が決まる。
        BotCommandParser.IsPeriodKey(value).Should().BeFalse();
    }

    [Fact]
    public void 入力全体の末尾にある改行は従来どおり_Trim_で落ちる()
    {
        // 変更しないもの: 末尾の空白類は Parse 冒頭の Trim が落とす。会話キーへ LF は残らない。
        var command = BotCommandParser.Parse("/report show daily-2026-08-28\n");

        command.Kind.Should().Be(BotCommandKind.ReportShow);
        command.PeriodKey.Should().Be("daily-2026-08-28");
    }
}
