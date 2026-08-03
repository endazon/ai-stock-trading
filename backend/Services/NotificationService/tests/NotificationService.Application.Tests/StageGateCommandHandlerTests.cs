using AiStockTrading.Notification.Application.Ports;
using AiStockTrading.Notification.Application.Services;
using AiStockTrading.Notification.Application.State;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.Notification.Application.Tests;

// FR-20, FR-14, UC-06, ADR-0007/0008, IADR-0070/0081: 段階ゲートコマンド処理の閂（多層認証→解析→Risk 呼び出し）。
// kill switch / pause と同水準の多層認証。確認（promote/demote のボタン）は Gateway が担い、ハンドラは確認済み前提で呼ばれる。
// 拒否時に Risk を呼ばない。
public class StageGateCommandHandlerTests
{
    private const string Guild = "guild-1";
    private const string Channel = "channel-1";
    private const string OwnerUser = "discord-owner-1";

    private sealed class FakeStageGateController : IStageGateController
    {
        public int StatusCalls { get; private set; }

        public int WithdrawalCalls { get; private set; }

        public int TransitionCalls { get; private set; }

        public int? LastTargetStage { get; private set; }

        public Task<StageGateStatusResult> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            StatusCalls++;
            return Task.FromResult(new StageGateStatusResult(true, "現段階: Stage 1（ペーパー）"));
        }

        public Task<StageTransitionCommandResult> RequestTransitionAsync(
            int targetStage, CancellationToken cancellationToken = default)
        {
            TransitionCalls++;
            LastTargetStage = targetStage;
            return Task.FromResult(new StageTransitionCommandResult(true, true, $"段階を Stage {targetStage} へ遷移しました。"));
        }

        public Task<StageGateStatusResult> EvaluateWithdrawalAsync(CancellationToken cancellationToken = default)
        {
            WithdrawalCalls++;
            return Task.FromResult(new StageGateStatusResult(true, "撤退評価: 撤退基準には抵触していません。"));
        }
    }

    private static DiscordBotOptions FullyConfigured()
    {
        var options = new DiscordBotOptions
        {
            GuildId = Guild,
            ChannelId = Channel,
        };
        options.AllowedUserIds.Add(OwnerUser);
        options.UserMapping[OwnerUser] = "endazon";
        return options;
    }

    private static StageGateCommandHandler Handler(IStageGateController controller, DiscordBotOptions options) =>
        new(controller, options, NullLogger<StageGateCommandHandler>.Instance);

    private static DiscordCommandContext Context(string raw, string? user = OwnerUser, bool isDm = false) =>
        new(Guild, Channel, user, isDm, raw);

    [Fact]
    public async Task 本人の段階照会は参照のみで遷移を呼ばない()
    {
        var controller = new FakeStageGateController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context("/stage status"));

        result.WasExecuted.Should().BeTrue();
        result.Message.Should().Contain("Stage 1");
        controller.StatusCalls.Should().Be(1);
        controller.TransitionCalls.Should().Be(0);
    }

    [Fact]
    public async Task 本人の昇格は遷移先つきで_transition_を呼ぶ()
    {
        var controller = new FakeStageGateController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context("/stage promote 2"));

        result.WasExecuted.Should().BeTrue();
        result.Accepted.Should().BeTrue();
        controller.TransitionCalls.Should().Be(1);
        controller.LastTargetStage.Should().Be(2);
    }

    [Fact]
    public async Task 本人の差し戻しは遷移先つきで_transition_を呼ぶ()
    {
        var controller = new FakeStageGateController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context("/stage demote 1"));

        result.WasExecuted.Should().BeTrue();
        controller.TransitionCalls.Should().Be(1);
        controller.LastTargetStage.Should().Be(1);
    }

    [Fact]
    public async Task 撤退評価は_withdrawal_を呼ぶ()
    {
        var controller = new FakeStageGateController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context("/stage withdrawal"));

        result.WasExecuted.Should().BeTrue();
        result.Message.Should().Contain("撤退評価");
        controller.WithdrawalCalls.Should().Be(1);
        controller.TransitionCalls.Should().Be(0);
    }

    [Fact]
    public async Task DM_では_Risk_を呼ばない()
    {
        var controller = new FakeStageGateController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context("/stage promote 2", isDm: true));

        result.WasExecuted.Should().BeFalse();
        controller.TransitionCalls.Should().Be(0);
    }

    [Fact]
    public async Task 許可リスト外のユーザーでは_Risk_を呼ばない()
    {
        var controller = new FakeStageGateController();

        var result = await Handler(controller, FullyConfigured())
            .HandleAsync(Context("/stage promote 2", user: "intruder"));

        result.WasExecuted.Should().BeFalse();
        controller.TransitionCalls.Should().Be(0);
    }

    [Fact]
    public async Task 多層認証の設定が空なら全拒否で_Risk_を呼ばない()
    {
        // 受け入れ基準2: 多層認証は kill switch と同水準。設定が空（GuildId 等未設定）なら全拒否。
        var controller = new FakeStageGateController();

        var result = await Handler(controller, new DiscordBotOptions()).HandleAsync(Context("/stage status"));

        result.WasExecuted.Should().BeFalse();
        controller.StatusCalls.Should().Be(0);
    }

    [Fact]
    public async Task Keycloak_マッピングが無ければ_Risk_を呼ばない()
    {
        var controller = new FakeStageGateController();
        var options = FullyConfigured();
        options.UserMapping.Remove(OwnerUser);

        var result = await Handler(controller, options).HandleAsync(Context("/stage status"));

        result.WasExecuted.Should().BeFalse();
        controller.StatusCalls.Should().Be(0);
    }

    [Fact]
    public async Task 段階ゲート以外のコマンドでは_Risk_を呼ばない()
    {
        // pause/kill switch など他系のコマンドは本ハンドラでは実行しない（他ハンドラが扱う）。
        var controller = new FakeStageGateController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context("/pause"));

        result.WasExecuted.Should().BeFalse();
        controller.StatusCalls.Should().Be(0);
        controller.TransitionCalls.Should().Be(0);
    }

    [Fact]
    public async Task 遷移先が範囲外の昇格は解析されず_Risk_を呼ばない()
    {
        // パーサが範囲外（4）を Unknown に倒すため、ハンドラは段階ゲート系として実行しない。
        var controller = new FakeStageGateController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context("/stage promote 4"));

        result.WasExecuted.Should().BeFalse();
        controller.TransitionCalls.Should().Be(0);
    }
}
