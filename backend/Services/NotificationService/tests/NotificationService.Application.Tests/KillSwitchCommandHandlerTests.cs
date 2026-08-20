using AiStockTrading.Notification.Application.Ports;
using AiStockTrading.Notification.Application.Services;
using AiStockTrading.Notification.Application.State;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.Notification.Application.Tests;

// FR-14, UC-06, IADR-0062: kill switch コマンド処理の閂（多層認証→解析→確認ステップ→Risk 呼び出し）。
// 受け入れ基準9: 起動済みへの再起動は副作用なし（冪等）。
// 併せて「拒否時に Risk を呼ばない」ことを検証する（誤爆防止の中核）。
public class KillSwitchCommandHandlerTests
{
    private const string Guild = "guild-1";
    private const string Channel = "channel-1";
    private const string OwnerUser = "discord-owner-1";
    private const string Phrase = "STOP TRADING";

    // 呼び出し回数を数える fake（Risk を呼んだか＝閂を通ったかの観測点）。
    private sealed class FakeKillSwitchController : IKillSwitchController
    {
        public int EngageCalls { get; private set; }

        public int DisengageCalls { get; private set; }

        public string? LastReason { get; private set; }

        public bool Engaged { get; private set; }

        public Task<KillSwitchResult> EngageAsync(string reason, CancellationToken cancellationToken = default)
        {
            EngageCalls++;
            LastReason = reason;
            // Risk 側は起動済みなら現状態を返すのみ（冪等）。ここでも同じ意味論を模す。
            Engaged = true;
            return Task.FromResult(new KillSwitchResult(true, true, "起動しました"));
        }

        public Task<KillSwitchResult> DisengageAsync(string reason, CancellationToken cancellationToken = default)
        {
            DisengageCalls++;
            LastReason = reason;
            Engaged = false;
            return Task.FromResult(new KillSwitchResult(true, false, "解除しました"));
        }
    }

    private static DiscordBotOptions FullyConfigured()
    {
        var options = new DiscordBotOptions
        {
            GuildId = Guild,
            ChannelId = Channel,
            KillSwitchConfirmationPhrase = Phrase,
        };
        options.AllowedUserIds.Add(OwnerUser);
        options.UserMapping[OwnerUser] = "endazon";
        return options;
    }

    private static KillSwitchCommandHandler Handler(IKillSwitchController controller, DiscordBotOptions options) =>
        new(controller, options, NullLogger<KillSwitchCommandHandler>.Instance);

    private static DiscordCommandContext Context(string raw = "/killswitch", string? user = OwnerUser, bool isDm = false) =>
        new(Guild, Channel, user, isDm, raw);

    [Fact]
    public async Task 本人が確認フレーズを入力すれば起動される()
    {
        var controller = new FakeKillSwitchController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context(), Phrase);

        result.WasExecuted.Should().BeTrue();
        result.Result!.Engaged.Should().BeTrue();
        controller.EngageCalls.Should().Be(1);
        // FR-11・ADR-0003: Risk 側は理由必須。actor（Keycloak 利用者名）を理由に残す。
        controller.LastReason.Should().Contain("endazon");
    }

    // #223, IADR-0097: 裁定（planning#35 / ADR-0009）により解除も確認フレーズを要する。
    [Fact]
    public async Task 本人が確認フレーズを入力すれば解除される()
    {
        var controller = new FakeKillSwitchController();

        var result = await Handler(controller, FullyConfigured())
            .HandleAsync(Context("/killswitch off"), Phrase);

        result.WasExecuted.Should().BeTrue();
        controller.DisengageCalls.Should().Be(1);
        controller.Engaged.Should().BeFalse();
        controller.LastReason.Should().Contain("endazon");
    }

    // #223, IADR-0097: 解除もフレーズ不一致では Risk を呼ばない（誤爆防止を解除方向にも掛ける）。
    [Fact]
    public async Task 解除も確認フレーズが不一致なら_Risk_を呼ばない()
    {
        var controller = new FakeKillSwitchController();

        var result = await Handler(controller, FullyConfigured())
            .HandleAsync(Context("/killswitch off"), "wrong phrase");

        result.WasExecuted.Should().BeFalse();
        controller.DisengageCalls.Should().Be(0);
    }

    // #223, IADR-0097: 解除もフレーズ未入力（null）では Risk を呼ばない。
    [Fact]
    public async Task 解除も確認フレーズなしなら_Risk_を呼ばない()
    {
        var controller = new FakeKillSwitchController();

        var result = await Handler(controller, FullyConfigured())
            .HandleAsync(Context("/killswitch off"), confirmationPhrase: null);

        result.WasExecuted.Should().BeFalse();
        controller.DisengageCalls.Should().Be(0);
    }

    // #223, IADR-0097（安全既定）: フレーズ未設定なら解除も拒否する（設定漏れで解除の閂が外れない）。
    [Fact]
    public async Task 解除も確認フレーズが未設定なら_Risk_を呼ばない()
    {
        var controller = new FakeKillSwitchController();
        var options = FullyConfigured();
        options.KillSwitchConfirmationPhrase = null;

        var result = await Handler(controller, options)
            .HandleAsync(Context("/killswitch off"), Phrase);

        result.WasExecuted.Should().BeFalse();
        controller.DisengageCalls.Should().Be(0);
    }

    // #223, IADR-0097: 冪等。正しいフレーズでの再解除も状態は解除のまま（controller が現状態を返す）。
    [Fact]
    public async Task 解除済みへの再解除は状態を変えない()
    {
        var controller = new FakeKillSwitchController();
        var handler = Handler(controller, FullyConfigured());

        var first = await handler.HandleAsync(Context("/killswitch off"), Phrase);
        var second = await handler.HandleAsync(Context("/killswitch off"), Phrase);

        first.Result!.Engaged.Should().BeFalse();
        second.Result!.Engaged.Should().BeFalse();
        controller.Engaged.Should().BeFalse();
        controller.DisengageCalls.Should().Be(2);
    }

    // 受け入れ基準9（冪等）: 起動済みへの再起動は状態を変えない。
    [Fact]
    public async Task 起動済みへの再起動は状態を変えない()
    {
        var controller = new FakeKillSwitchController();
        var handler = Handler(controller, FullyConfigured());

        var first = await handler.HandleAsync(Context(), Phrase);
        var second = await handler.HandleAsync(Context(), Phrase);

        first.Result!.Engaged.Should().BeTrue();
        second.Result!.Engaged.Should().BeTrue();
        controller.Engaged.Should().BeTrue();
    }

    // 以下、閂が Risk を呼ばせないことの検証（誤爆防止の中核）。
    [Fact]
    public async Task 許可リスト外のユーザーでは_Risk_を呼ばない()
    {
        var controller = new FakeKillSwitchController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context(user: "intruder"), Phrase);

        result.WasExecuted.Should().BeFalse();
        controller.EngageCalls.Should().Be(0);
    }

    [Fact]
    public async Task DM_では_Risk_を呼ばない()
    {
        var controller = new FakeKillSwitchController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context(isDm: true), Phrase);

        result.WasExecuted.Should().BeFalse();
        controller.EngageCalls.Should().Be(0);
    }

    [Fact]
    public async Task 確認フレーズが不一致なら_Risk_を呼ばない()
    {
        var controller = new FakeKillSwitchController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context(), "wrong phrase");

        result.WasExecuted.Should().BeFalse();
        controller.EngageCalls.Should().Be(0);
    }

    // 安全既定: 確認フレーズ未設定なら起動しない（設定漏れで閂が外れない）。
    [Fact]
    public async Task 確認フレーズが未設定なら_Risk_を呼ばない()
    {
        var controller = new FakeKillSwitchController();
        var options = FullyConfigured();
        options.KillSwitchConfirmationPhrase = null;

        var result = await Handler(controller, options).HandleAsync(Context(), Phrase);

        result.WasExecuted.Should().BeFalse();
        controller.EngageCalls.Should().Be(0);
    }

    [Fact]
    public async Task 未知のコマンドでは_Risk_を呼ばない()
    {
        var controller = new FakeKillSwitchController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context("/killswitch of"), Phrase);

        result.WasExecuted.Should().BeFalse();
        controller.EngageCalls.Should().Be(0);
        controller.DisengageCalls.Should().Be(0);
    }

    // 既定設定（何も構成しない）では、たとえ正しいフレーズでも起動しない。
    [Fact]
    public async Task 既定の設定では_Risk_を呼ばない()
    {
        var controller = new FakeKillSwitchController();

        var result = await Handler(controller, new DiscordBotOptions()).HandleAsync(Context(), Phrase);

        result.WasExecuted.Should().BeFalse();
        controller.EngageCalls.Should().Be(0);
    }
}
