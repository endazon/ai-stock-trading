using AiStockTrading.Notification.Application.Services;
using AiStockTrading.Notification.Application.State;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Notification.Application.Tests;

// FR-14, UC-06, IADR-0062 決定5: 高リスク操作の確認ステップ（確認フレーズ）。
// 受け入れ基準6: 不一致では起動しない／受け入れ基準7: 未設定なら起動しない。
public class KillSwitchConfirmationTests
{
    private const string Phrase = "STOP TRADING";

    private static DiscordBotOptions WithPhrase(string? phrase) =>
        new() { KillSwitchConfirmationPhrase = phrase };

    [Fact]
    public void 確認フレーズが一致すれば確認済みになる()
    {
        KillSwitchConfirmation.Verify(Phrase, WithPhrase(Phrase)).IsConfirmed.Should().BeTrue();
    }

    // 照合は前後空白と大小文字のみ正規化する。
    [Theory]
    [InlineData("  STOP TRADING  ")]
    [InlineData("stop trading")]
    public void 前後空白と大小文字は吸収する(string entered)
    {
        KillSwitchConfirmation.Verify(entered, WithPhrase(Phrase)).IsConfirmed.Should().BeTrue();
    }

    // 受け入れ基準6: 不一致・空は起動しない。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("STOP")]
    [InlineData("STOP  TRADING")]
    [InlineData("STOP TRADING!")]
    public void 確認フレーズが不一致なら起動しない(string? entered)
    {
        KillSwitchConfirmation.Verify(entered, WithPhrase(Phrase)).IsConfirmed.Should().BeFalse();
    }

    // 受け入れ基準7（安全既定）: 未設定を「フレーズ不要」と解釈しない。設定漏れで閂が外れないこと。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 確認フレーズが未設定なら何を入力しても起動しない(string? configured)
    {
        var result = KillSwitchConfirmation.Verify("STOP TRADING", WithPhrase(configured));

        result.IsConfirmed.Should().BeFalse();
        result.Reason.Should().Contain("未設定");
    }
}
