using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// FR-09, FR-11, NFR（セキュリティ）, #313 / #318 / #339, IADR-0227:
// **監査台帳へ秘匿情報を入れない**ことの構造テスト（否定形）。
//
// 監査台帳（`audit_events`）は**契約イベント全量を JSON で 7 年保持する**（NFR-10）。
// したがって秘匿情報が契約イベントの項目として 1 つでも生まれたら、**7 年消せない場所へ入る**。
// Webhook URL の露出（#313 / #318）は送信ログ側では #289 が構造的に解消したが、
// **契約イベント側は誰も見ていなかった** —— そこを塞ぐ。
//
// 🔴 **値ではなくプロパティ名の全数走査で見る。** 値の検査は「そのテストが用意した値」しか見ないが、
// プロパティ名は**契約が持ち得る場所そのもの**であり、母集合が閉じている。
public class AuditPayloadSecretExposureTests
{
    // 秘匿情報を運ぶ項目に付きやすい語。**大文字小文字を無視して部分一致**で見る。
    private static readonly string[] SecretLikeNames =
    [
        "webhook",
        "token",
        "secret",
        "password",
        "passwd",
        "credential",
        "apikey",
        "accesskey",
        "privatekey",
        "connectionstring",
        "authorization",
        "bearer",
    ];

    [Fact]
    public void 否定形_契約イベントに秘匿情報を示す名のプロパティは_1_つも無い()
    {
        var offenders = EventTypeDiscovery.GetEventTypes()
            .SelectMany(t => t.GetProperties().Select(p => (Event: t.Name, Property: p.Name)))
            .Where(x => SecretLikeNames.Any(
                s => x.Property.Contains(s, StringComparison.OrdinalIgnoreCase)))
            .Select(x => $"{x.Event}.{x.Property}")
            .ToList();

        offenders.Should().BeEmpty(
            "契約イベントは監査台帳へ全量 JSON で 7 年保持される（NFR-10）。"
            + "秘匿情報を運ぶ項目を契約へ足すと、**7 年消せない場所へ秘密が入る**。"
            + "秘匿情報は契約に載せず、送信側で保持して伏せること（#289 の Webhook クライアントと同じ作法）。"
            + "\n該当: " + string.Join(", ", offenders));
    }

    // 走査そのものが機能していることの証明（母集合が空になって無条件で緑になる経路を塞ぐ）。
    [Fact]
    public void 走査の母集合は空ではない()
    {
        EventTypeDiscovery.GetEventTypes().Should().NotBeEmpty();
        EventTypeDiscovery.GetEventTypes()
            .SelectMany(t => t.GetProperties()).Should().NotBeEmpty();
    }

    // 検出器が load-bearing であること（＝実際に秘匿名を検出できること）の構造的証明。
    [Theory]
    [InlineData("WebhookUrl")]
    [InlineData("DiscordWebhook")]
    [InlineData("AccessToken")]
    [InlineData("clientSecret")]
    [InlineData("ApiKey")]
    public void 検出器は秘匿情報を示す名を実際に検出する(string propertyName)
    {
        SecretLikeNames.Any(s => propertyName.Contains(s, StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("Symbol")]
    [InlineData("AmountUsd")]
    [InlineData("DecisionId")]
    public void 検出器は通常の項目名を誤検出しない(string propertyName)
    {
        SecretLikeNames.Any(s => propertyName.Contains(s, StringComparison.OrdinalIgnoreCase))
            .Should().BeFalse();
    }
}
