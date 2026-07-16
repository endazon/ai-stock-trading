using AiStockTrading.Notification.Worker.Composable.Adapters;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AiStockTrading.Notification.Worker.Tests;

// FR-14, IADR-0063: 構成の読み取り。未設定が「拒否側の既定」のまま残ることを固定する（補完しない）。
public class DiscordBotOptionsReaderTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void 空の構成は既定のまま_全拒否側になる()
    {
        var options = DiscordBotOptionsReader.Read(Config([]));

        options.Enabled.Should().BeFalse();
        options.IsGatewayEnabled.Should().BeFalse();
        options.GuildId.Should().BeNull();
        options.ChannelId.Should().BeNull();
        options.AllowedUserIds.Should().BeEmpty();
        options.UserMapping.Should().BeEmpty();
        options.KillSwitchConfirmationPhrase.Should().BeNull();
    }

    [Fact]
    public void 構成値を読み取る()
    {
        var options = DiscordBotOptionsReader.Read(Config(new Dictionary<string, string?>
        {
            ["Notifications:Discord:Bot:Enabled"] = "true",
            ["Notifications:Discord:Bot:Token"] = "bot-token",
            ["Notifications:Discord:Bot:GuildId"] = "111",
            ["Notifications:Discord:Bot:ChannelId"] = "222",
            ["Notifications:Discord:Bot:KillSwitchConfirmationPhrase"] = "STOP TRADING",
            ["Notifications:Discord:Bot:UserMapping:333"] = "endazon",
        }));

        options.Enabled.Should().BeTrue();
        options.IsGatewayEnabled.Should().BeTrue();
        options.GuildId.Should().Be("111");
        options.ChannelId.Should().Be("222");
        options.KillSwitchConfirmationPhrase.Should().Be("STOP TRADING");
        options.UserMapping.Should().ContainKey("333").WhoseValue.Should().Be("endazon");
    }

    // 配列形式（appsettings / helm values 向け）。
    [Fact]
    public void 許可ユーザーIDを配列で読み取る()
    {
        var options = DiscordBotOptionsReader.Read(Config(new Dictionary<string, string?>
        {
            ["Notifications:Discord:Bot:AllowedUserIds:0"] = "333",
            ["Notifications:Discord:Bot:AllowedUserIds:1"] = "444",
        }));

        options.AllowedUserIds.Should().BeEquivalentTo("333", "444");
    }

    // カンマ区切り（環境変数 / .env 向け。配列添字より 1 変数で与えられる方が扱いやすい）。
    [Fact]
    public void 許可ユーザーIDをカンマ区切りで読み取る()
    {
        var options = DiscordBotOptionsReader.Read(Config(new Dictionary<string, string?>
        {
            ["Notifications:Discord:Bot:AllowedUserIds"] = " 333 , 444 ",
        }));

        options.AllowedUserIds.Should().BeEquivalentTo("333", "444");
    }

    // Enabled は厳密に true のときのみ有効（不正値を「有効」と解釈しない）。
    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData("TRUE!")]
    public void Enabled_が不正値なら無効に倒す(string raw)
    {
        var options = DiscordBotOptionsReader.Read(Config(new Dictionary<string, string?>
        {
            ["Notifications:Discord:Bot:Enabled"] = raw,
        }));

        options.Enabled.Should().BeFalse();
    }
}
