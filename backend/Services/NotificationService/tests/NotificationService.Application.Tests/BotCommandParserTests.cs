using AiStockTrading.Notification.Application.Services;
using AiStockTrading.Notification.Application.State;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.Notification.Application.Tests;

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
    public void 未知のコマンドは_Unknown_になる(string? raw)
    {
        BotCommandParser.Parse(raw).Kind.Should().Be(BotCommandKind.Unknown);
    }
}
