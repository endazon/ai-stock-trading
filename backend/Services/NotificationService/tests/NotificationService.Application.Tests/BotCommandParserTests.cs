using AiStockTrading.Notification.Application.Services;
using AiStockTrading.Notification.Application.State;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.Notification.Application.Tests;

// FR-14, UC-06: スラッシュコマンドの解析。受け入れ基準8: /killswitch・/killswitch off の解析と未知コマンドの拒否。
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

    // 未知・空・typo は Unknown に倒し、呼び出し側で拒否する（暗黙に何かを実行しない）。
    // 特に "/killswitch of"（typo）が起動として解釈されないことが重要（誤爆防止）。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/report approve")]
    [InlineData("/status")]
    [InlineData("/killswitch of")]
    [InlineData("/killswitch on")]
    [InlineData("/killswitch off now")]
    public void 未知のコマンドは_Unknown_になる(string? raw)
    {
        BotCommandParser.Parse(raw).Kind.Should().Be(BotCommandKind.Unknown);
    }
}
