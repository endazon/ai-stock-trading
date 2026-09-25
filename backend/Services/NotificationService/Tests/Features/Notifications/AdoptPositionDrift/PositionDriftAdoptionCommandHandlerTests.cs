using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationService.Domain;
using NotificationService.Features.Notifications;
using NotificationService.Features.Notifications.AdoptPositionDrift;
using Xunit;

namespace NotificationService.Tests;

// 🔴 T-10-988, FR-10, FR-11, FR-14, UC-06, ADR-0003, ADR-0041 決定 4, #871, IADR-0240 決定11, IADR-0383, IADR-0423:
// 乖離の取り込みコマンドの閂（多層認証 → コマンド解析 → 確認フレーズ → 理由必須 → リスク管理の呼び出し）。
//
// **拒否時にリスク管理を呼ばないこと**が本テスト群の主眼である——閂が「呼んだ後で無視する」形なら、台帳が
// 実際に書き換わってしまう（＝「確認を経ずに台帳が変わらない」「許可外の利用者は実行できない」の否定形）。
// 併せて、**記録の内容を API 経由と揃える**ための 2 点（理由文を加工しない・操作者を onBehalfOf で運ぶ）を固定する。
public class PositionDriftAdoptionCommandHandlerTests
{
    private const string Guild = "guild-1";
    private const string Channel = "channel-1";
    private const string OwnerUser = "discord-owner-1";
    private const string Phrase = "STOP TRADING";
    private const string Reason = "証券会社のアプリで全株を売却した";

    private sealed class FakeController(PositionDriftAdoptionResult? result = null) : IPositionDriftAdoptionController
    {
        public int Calls { get; private set; }

        public (string Symbol, Market Market, string Reason, string OnBehalfOf)? Last { get; private set; }

        public Task<PositionDriftAdoptionResult> AdoptAsync(
            string symbol, Market market, string reason, string onBehalfOf, CancellationToken cancellationToken = default)
        {
            Calls++;
            Last = (symbol, market, reason, onBehalfOf);
            return Task.FromResult(result ?? new PositionDriftAdoptionResult(true, true, "台帳の AAPL（米国市場）の建玉を 100 → 0 へ合わせました。"));
        }
    }

    // 確認フレーズは kill switch と**同じ設定値**を用いる（GFV 解除と同じ。設定点を増やすと設定漏れの面が増える）。
    private static DiscordBotOptions FullyConfigured(string? phrase = Phrase)
    {
        var options = new DiscordBotOptions
        {
            GuildId = Guild,
            ChannelId = Channel,
            KillSwitchConfirmationPhrase = phrase,
        };
        options.AllowedUserIds.Add(OwnerUser);
        options.UserMapping[OwnerUser] = "endazon";
        return options;
    }

    private static PositionDriftAdoptionCommandHandler Handler(
        IPositionDriftAdoptionController controller, DiscordBotOptions options) =>
        new(controller, options, NullLogger<PositionDriftAdoptionCommandHandler>.Instance);

    private static DiscordCommandContext Context(
        string raw = "/drift adopt AAPL us", string? user = OwnerUser, bool isDm = false, string channel = Channel) =>
        new(Guild, channel, user, isDm, raw);

    // ---- 受理の経路 ----

    [Fact]
    public async Task 本人が理由と確認フレーズを添えれば取り込みを要求する()
    {
        var controller = new FakeController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context(), Phrase, Reason);

        result.WasExecuted.Should().BeTrue();
        result.Adopted.Should().BeTrue();
        controller.Calls.Should().Be(1);
        controller.Last!.Value.Symbol.Should().Be("AAPL");
        controller.Last.Value.Market.Should().Be(Market.UnitedStates);
    }

    // 🔴 記録の内容を API 経由と揃える: **理由文は加工しない**（GFV のような `actor=` の併記をしない）。
    // 操作者は構造化した欄（onBehalfOf）で、**多層認証が解決した Keycloak 利用者名**を運ぶ（Discord のユーザー ID ではない）。
    [Fact]
    public async Task 理由文は加工せず_操作者は多層認証の利用者名を_onBehalfOf_で運ぶ()
    {
        var controller = new FakeController();

        await Handler(controller, FullyConfigured()).HandleAsync(Context(), Phrase, $"  {Reason}  ");

        controller.Last!.Value.Reason.Should().Be(Reason, "前後の空白だけを落とし、文面は変えない");
        controller.Last.Value.Reason.Should().NotContain("actor=").And.NotContain("Discord");
        controller.Last.Value.OnBehalfOf.Should().Be("endazon");
    }

    [Fact]
    public async Task 確認フレーズの照合は大小文字と前後空白を吸収する()
    {
        var controller = new FakeController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context(), "  stop trading ", Reason);

        result.WasExecuted.Should().BeTrue();
        controller.Calls.Should().Be(1);
    }

    // リスク管理の拒否（422）は「実行された」として、その理由の文言をそのまま利用者へ返す。
    [Fact]
    public async Task リスク管理の拒否は理由の文言をそのまま返す()
    {
        var rejection = new PositionDriftAdoptionResult(
            true, false, "取り込みは行いませんでした（台帳は変わっていません）: ブローカ建玉の最新の観測が古すぎます（60 分超）。");
        var controller = new FakeController(rejection);

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context(), Phrase, Reason);

        result.WasExecuted.Should().BeTrue();
        result.Adopted.Should().BeFalse();
        result.Message.Should().Be(rejection.Message);
    }

    // ---- 🔴 否定形: いずれもリスク管理を呼ばない（＝台帳は変わらない） ----

    public static TheoryData<string, DiscordCommandContext> Unauthorized() => new()
    {
        { "DM", Context(isDm: true) },
        { "許可リスト外", Context(user: "stranger") },
        { "ユーザー不明", Context(user: null) },
        { "専用チャンネル以外", Context(channel: "other-channel") },
    };

    [Theory]
    [MemberData(nameof(Unauthorized))]
    public async Task 許可外の利用者は実行できない(string because, DiscordCommandContext context)
    {
        var controller = new FakeController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(context, Phrase, Reason);

        result.WasExecuted.Should().BeFalse(because);
        controller.Calls.Should().Be(0, because);
    }

    [Fact]
    public async Task Keycloak_利用者への対応付けが無い利用者は実行できない()
    {
        var controller = new FakeController();
        var options = FullyConfigured();
        options.UserMapping.Clear();

        var result = await Handler(controller, options).HandleAsync(Context(), Phrase, Reason);

        result.WasExecuted.Should().BeFalse();
        controller.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("違うフレーズ")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task 確認フレーズが一致しなければ実行しない(string? phrase)
    {
        var controller = new FakeController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context(), phrase, Reason);

        result.WasExecuted.Should().BeFalse();
        controller.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 確認フレーズが未設定なら安全既定で実行しない(string? configured)
    {
        var controller = new FakeController();

        var result = await Handler(controller, FullyConfigured(configured)).HandleAsync(Context(), Phrase, Reason);

        result.WasExecuted.Should().BeFalse();
        controller.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task 理由が無ければ実行しない(string? reason)
    {
        var controller = new FakeController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context(), Phrase, reason);

        result.WasExecuted.Should().BeFalse();
        controller.Calls.Should().Be(0);
    }

    // 別種のコマンド・書式外の対象は、確認フレーズが正しくても取り込みへ落ちない。
    [Theory]
    [InlineData("/killswitch")]
    [InlineData("/gfv clear")]
    [InlineData("/stage promote 2")]
    [InlineData("/drift adopt AAPL")]
    [InlineData("/drift adopt AAPL hk")]
    [InlineData("/drift adopt AAPL us 100")]
    [InlineData("/drift adopt AA;PL us")]
    [InlineData("")]
    public async Task 取り込み以外や書式外のコマンドは実行しない(string raw)
    {
        var controller = new FakeController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context(raw), Phrase, Reason);

        result.WasExecuted.Should().BeFalse();
        controller.Calls.Should().Be(0);
    }
}
