using NotificationService.Application.Ports;
using NotificationService.Application.Services;
using NotificationService.Application.State;
using NotificationService.Infrastructure.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NotificationService.Infrastructure.Tests;

// FR-14, IADR-0062 決定1: Bot Gateway の安全既定。受け入れ基準11: 既定で接続しない／設定不備でも接続しない。
// 実 Discord への接続は行わない（Create は接続せずインスタンスを返すのみ）。
public class DiscordBotGatewayFactoryTests
{
    private const string Phrase = "STOP TRADING";
    private const string OwnerUser = "discord-owner-1";

    private static DiscordBotOptions FullyConfigured()
    {
        var options = new DiscordBotOptions
        {
            Enabled = true,
            Token = "bot-token",
            GuildId = "1",
            ChannelId = "2",
            KillSwitchConfirmationPhrase = Phrase,
        };
        options.AllowedUserIds.Add(OwnerUser);
        options.UserMapping[OwnerUser] = "endazon";
        return options;
    }

    private static IDiscordBotGateway Create(DiscordBotOptions options)
    {
        var handler = new KillSwitchCommandHandler(
            new StubKillSwitchController(), options, NullLogger<KillSwitchCommandHandler>.Instance);
        var pauseHandler = new PauseCommandHandler(
            new StubPauseController(), options, NullLogger<PauseCommandHandler>.Instance);
        var stageGateHandler = new StageGateCommandHandler(
            new StubStageGateController(), options, NullLogger<StageGateCommandHandler>.Instance);
        // #464, ADR-0028 決定3: GFV 解除も Discord Bot の窓口に載る（kill switch と同水準）。
        var gfvHandler = new GoodFaithViolationCommandHandler(
            new StubGoodFaithViolationController(), options,
            NullLogger<GoodFaithViolationCommandHandler>.Instance);
        // #341, IADR-0240: 報告書レビュー（版番号の照会・冪等確定・差し戻し）も同じ Gateway に載る。
        var reportHandler = new ReportCommandHandler(
            new StubReportReviewController(), new VersionedConfirmationGuard(), options,
            NullLogger<ReportCommandHandler>.Instance);
        return DiscordBotGatewayFactory.Create(
            options, handler, pauseHandler, stageGateHandler, gfvHandler, reportHandler,
            NullLoggerFactory.Instance);
    }

    // 受け入れ基準11: 何も設定しなければ接続しない。
    [Fact]
    public void 既定では_Gateway_に接続しない()
    {
        Create(new DiscordBotOptions()).Should().BeOfType<NullDiscordBotGateway>();
    }

    [Fact]
    public void Enabled_が_false_なら接続しない()
    {
        var options = FullyConfigured();
        options.Enabled = false;

        Create(options).Should().BeOfType<NullDiscordBotGateway>();
    }

    [Fact]
    public void Token_が未設定なら接続しない()
    {
        var options = FullyConfigured();
        options.Token = null;

        Create(options).Should().BeOfType<NullDiscordBotGateway>();
    }

    // 設定漏れの Bot をオンラインにしない（多層認証の設定が1つでも欠ければ接続しない）。
    [Fact]
    public void GuildId_が未設定なら接続しない()
    {
        var options = FullyConfigured();
        options.GuildId = null;

        Create(options).Should().BeOfType<NullDiscordBotGateway>();
    }

    [Fact]
    public void ChannelId_が未設定なら接続しない()
    {
        var options = FullyConfigured();
        options.ChannelId = null;

        Create(options).Should().BeOfType<NullDiscordBotGateway>();
    }

    [Fact]
    public void 許可ユーザーが空なら接続しない()
    {
        var options = FullyConfigured();
        options.AllowedUserIds.Clear();

        Create(options).Should().BeOfType<NullDiscordBotGateway>();
    }

    // 許可ユーザーに Keycloak 対応付けが無ければ、その利用者は操作できない＝設定不備として接続しない。
    [Fact]
    public void 許可ユーザーに_Keycloak_マッピングが無ければ接続しない()
    {
        var options = FullyConfigured();
        options.UserMapping.Clear();

        Create(options).Should().BeOfType<NullDiscordBotGateway>();
    }

    // kill switch の閂（確認フレーズ）が無ければ起動できないため、接続前に落とす。
    [Fact]
    public void 確認フレーズが未設定なら接続しない()
    {
        var options = FullyConfigured();
        options.KillSwitchConfirmationPhrase = null;

        Create(options).Should().BeOfType<NullDiscordBotGateway>();
    }

    // すべて揃った時のみ実 Gateway の実装を返す（この時点では接続しない）。
    [Fact]
    public void 設定が揃えば実_Gateway_を返す()
    {
        Create(FullyConfigured()).Should().BeOfType<DiscordNetBotGateway>();
    }

    // no-op は接続せず、Start/Stop が例外を投げない。
    [Fact]
    public async Task no_op_は起動停止しても何も起きない()
    {
        var gateway = Create(new DiscordBotOptions());

        await gateway.StartAsync();
        await gateway.StopAsync();
    }

    private sealed class StubKillSwitchController : IKillSwitchController
    {
        public Task<KillSwitchResult> EngageAsync(string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(new KillSwitchResult(true, true, "起動"));

        public Task<KillSwitchResult> DisengageAsync(string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(new KillSwitchResult(true, false, "解除"));
    }

    private sealed class StubPauseController : IPauseController
    {
        public Task<PauseResult> PauseAsync(string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PauseResult(true, true, "一時停止"));

        public Task<PauseResult> ResumeAsync(string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PauseResult(true, false, "再開"));

        public Task<RiskStatusResult> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RiskStatusResult(true, "稼働状態"));
    }

    private sealed class StubStageGateController : IStageGateController
    {
        public Task<StageGateStatusResult> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StageGateStatusResult(true, "段階ゲート"));

        public Task<StageTransitionCommandResult> RequestTransitionAsync(
            int targetStage, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StageTransitionCommandResult(true, true, "遷移"));

        public Task<StageGateStatusResult> EvaluateWithdrawalAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StageGateStatusResult(true, "撤退評価"));
    }

    // #464: 本テストは Gateway の接続可否だけを見るため、Risk は呼ばれない（呼ばれたら設計の誤りである）。
    private sealed class StubGoodFaithViolationController : IGoodFaithViolationController
    {
        public Task<GoodFaithViolationClearResult> ClearAsync(
            string reason, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Gateway の生成では Risk を呼ばない。");
    }

    // #341: 同上。Gateway の生成では報告書サービスを呼ばない（呼ばれたら設計の誤りである）。
    private sealed class StubReportReviewController : IReportReviewController
    {
        public Task<ReportReviewResult> GetReviewAsync(
            string periodKey, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Gateway の生成では報告書サービスを呼ばない。");

        public Task<ReportConfirmResult> ConfirmAsync(
            string periodKey, int expectedVersion, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Gateway の生成では報告書サービスを呼ばない。");

        public Task<ReportReviewResult> RequestChangesAsync(
            string periodKey, int expectedVersion, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Gateway の生成では報告書サービスを呼ばない。");
    }
}
