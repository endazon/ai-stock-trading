using NotificationService.Domain;
using NotificationService.Features.Notifications;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NotificationService.Tests;

// FR-14, UC-06, UC-07, ADR-0009, IADR-0075: 一時停止/再開・状態照会コマンド処理の閂（多層認証→解析→Risk 呼び出し）。
// kill switch と同水準の多層認証。ただし確認フレーズは要求しない（確認ボタンのみ）。拒否時に Risk を呼ばない。
public class PauseCommandHandlerTests
{
    private const string Guild = "guild-1";
    private const string Channel = "channel-1";
    private const string OwnerUser = "discord-owner-1";

    private sealed class FakePauseController : IPauseController
    {
        public int PauseCalls { get; private set; }

        public int ResumeCalls { get; private set; }

        public int StatusCalls { get; private set; }

        public string? LastReason { get; private set; }

        public Task<PauseResult> PauseAsync(string reason, CancellationToken cancellationToken = default)
        {
            PauseCalls++;
            LastReason = reason;
            return Task.FromResult(new PauseResult(true, true, "一時停止しました"));
        }

        public Task<PauseResult> ResumeAsync(string reason, CancellationToken cancellationToken = default)
        {
            ResumeCalls++;
            LastReason = reason;
            return Task.FromResult(new PauseResult(true, false, "再開しました"));
        }

        public Task<RiskStatusResult> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            StatusCalls++;
            return Task.FromResult(new RiskStatusResult(true, "稼働状態: 新規建て=可能"));
        }
    }

    // 確認フレーズは pause では不要のため設定しない（kill switch と異なる点）。
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

    private static PauseCommandHandler Handler(IPauseController controller, DiscordBotOptions options) =>
        new(controller, options, NullLogger<PauseCommandHandler>.Instance);

    private static DiscordCommandContext Context(string raw, string? user = OwnerUser, bool isDm = false) =>
        new(Guild, Channel, user, isDm, raw);

    [Fact]
    public async Task 本人の一時停止は確認フレーズなしで実行される()
    {
        var controller = new FakePauseController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context("/pause"));

        result.WasExecuted.Should().BeTrue();
        result.Result!.Paused.Should().BeTrue();
        controller.PauseCalls.Should().Be(1);
        // FR-11・ADR-0009: Risk 側は理由必須。actor（Keycloak 利用者名）を理由に残す。
        controller.LastReason.Should().Contain("endazon");
    }

    [Fact]
    public async Task 本人の再開は実行される()
    {
        var controller = new FakePauseController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context("/resume"));

        result.WasExecuted.Should().BeTrue();
        controller.ResumeCalls.Should().Be(1);
        controller.PauseCalls.Should().Be(0);
    }

    [Fact]
    public async Task 状態照会は参照のみでpause_resumeを呼ばない()
    {
        // UC-07/ADR-0009: /status は表示専用。副作用（pause/resume）を起こさない。
        var controller = new FakePauseController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context("/status"));

        result.WasExecuted.Should().BeTrue();
        result.Message.Should().Contain("稼働状態");
        controller.StatusCalls.Should().Be(1);
        controller.PauseCalls.Should().Be(0);
        controller.ResumeCalls.Should().Be(0);
    }

    [Fact]
    public async Task DM_では_Risk_を呼ばない()
    {
        var controller = new FakePauseController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context("/pause", isDm: true));

        result.WasExecuted.Should().BeFalse();
        controller.PauseCalls.Should().Be(0);
    }

    [Fact]
    public async Task 許可リスト外のユーザーでは_Risk_を呼ばない()
    {
        var controller = new FakePauseController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context("/pause", user: "intruder"));

        result.WasExecuted.Should().BeFalse();
        controller.PauseCalls.Should().Be(0);
    }

    [Fact]
    public async Task 多層認証の設定が空なら全拒否で_Risk_を呼ばない()
    {
        // 受け入れ基準4: 多層認証は kill switch と同水準。設定が空（GuildId 等未設定）なら全拒否。
        var controller = new FakePauseController();

        var result = await Handler(controller, new DiscordBotOptions()).HandleAsync(Context("/pause"));

        result.WasExecuted.Should().BeFalse();
        controller.PauseCalls.Should().Be(0);
    }

    [Fact]
    public async Task Keycloak_マッピングが無ければ_Risk_を呼ばない()
    {
        // 認証層5: actor を特定できないユーザーは拒否（監査に本人を残せないため）。
        var controller = new FakePauseController();
        var options = FullyConfigured();
        options.UserMapping.Remove(OwnerUser);

        var result = await Handler(controller, options).HandleAsync(Context("/pause"));

        result.WasExecuted.Should().BeFalse();
        controller.PauseCalls.Should().Be(0);
    }

    [Fact]
    public async Task 未知のコマンドでは_Risk_を呼ばない()
    {
        var controller = new FakePauseController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context("/killswitch"));

        result.WasExecuted.Should().BeFalse();
        controller.PauseCalls.Should().Be(0);
        controller.ResumeCalls.Should().Be(0);
    }
}
