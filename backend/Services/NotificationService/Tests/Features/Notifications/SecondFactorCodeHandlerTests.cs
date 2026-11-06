using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using NotificationService.Domain;
using NotificationService.Features.Notifications;
using NotificationService.Features.Notifications.AdoptPositionDrift;
using NotificationService.Features.Notifications.ClearGoodFaithViolations;
using NotificationService.Features.Notifications.OperateKillSwitch;
using NotificationService.Features.Notifications.OperateStageGate;
using NotificationService.Features.Notifications.OperateTradingPause;
using NotificationService.Features.Notifications.ReviewReport;
using NotificationService.Features.Notifications.RevisePolicy;
using Xunit;

namespace NotificationService.Tests;

// NFR-06, FR-14, UC-06, ADR-0009, #1552, IADR-0615: ハンドラが確認の画面で入れた 6 桁を、**取引を再開・緩める 7 操作**の口へだけ渡し、
// 止める向き（kill switch の起動・一時停止・差し戻し）には渡さないこと。ボットはコードを確かめず（確かめるのは提供側）、
// **ログに出さない**こと。閂（多層認証・確認フレーズ・理由）で止まったときは提供側を呼ばない＝コードも送らない。
public class SecondFactorCodeHandlerTests
{
    private const string Guild = "guild-1";
    private const string Channel = "channel-1";
    private const string OwnerUser = "discord-owner-1";
    private const string Phrase = "STOP TRADING";

    // 6 桁の形の試験用の値（鍵から作ったものではない）。
    private const string Code = "975310";

    private static DiscordBotOptions Options()
    {
        var options = new DiscordBotOptions { GuildId = Guild, ChannelId = Channel, KillSwitchConfirmationPhrase = Phrase };
        options.AllowedUserIds.Add(OwnerUser);
        options.UserMapping[OwnerUser] = "endazon";
        return options;
    }

    private static DiscordCommandContext Context(string raw, string? user = OwnerUser) => new(Guild, Channel, user, false, raw);

    // ---- ログの観測（コードが 1 文字も出ないこと） ----

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(",", pairs.Select(p => $"{p.Key}={p.Value}"))
                : string.Empty));
    }

    // ---- 偽の口（新しい口でコードを受け取る。従来の口は「コードなし」として数える） ----

    private sealed class KillSwitch : IKillSwitchController
    {
        public List<(string Op, string? Code)> Calls { get; } = [];

        public Task<KillSwitchResult> EngageAsync(string reason, CancellationToken cancellationToken = default)
        {
            Calls.Add(("engage", null));
            return Task.FromResult(new KillSwitchResult(true, true, "起動しました"));
        }

        public Task<KillSwitchResult> DisengageAsync(string reason, CancellationToken cancellationToken = default) =>
            DisengageAsync(reason, null, cancellationToken);

        public Task<KillSwitchResult> DisengageAsync(string reason, string? secondFactorCode, CancellationToken cancellationToken = default)
        {
            Calls.Add(("disengage", secondFactorCode));
            return Task.FromResult(new KillSwitchResult(true, false, "解除しました"));
        }
    }

    private sealed class Pause : IPauseController
    {
        public List<(string Op, string? Code)> Calls { get; } = [];

        public Task<PauseResult> PauseAsync(string reason, CancellationToken cancellationToken = default)
        {
            Calls.Add(("pause", null));
            return Task.FromResult(new PauseResult(true, true, "一時停止しました"));
        }

        public Task<PauseResult> ResumeAsync(string reason, CancellationToken cancellationToken = default) => ResumeAsync(reason, null, cancellationToken);

        public Task<PauseResult> ResumeAsync(string reason, string? secondFactorCode, CancellationToken cancellationToken = default)
        {
            Calls.Add(("resume", secondFactorCode));
            return Task.FromResult(new PauseResult(true, false, "再開しました"));
        }

        public Task<RiskStatusResult> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RiskStatusResult(true, "status"));
    }

    private sealed class Stage : IStageGateController
    {
        public List<(int Target, string? Code)> Calls { get; } = [];

        public Task<StageGateStatusResult> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StageGateStatusResult(true, "status"));

        public Task<StageTransitionCommandResult> RequestTransitionAsync(int targetStage, string onBehalfOf, CancellationToken cancellationToken = default) =>
            RequestTransitionAsync(targetStage, onBehalfOf, null, cancellationToken);

        public Task<StageTransitionCommandResult> RequestTransitionAsync(
            int targetStage, string onBehalfOf, string? secondFactorCode, CancellationToken cancellationToken = default)
        {
            Calls.Add((targetStage, secondFactorCode));
            return Task.FromResult(new StageTransitionCommandResult(true, true, "受理"));
        }

        public Task<StageGateStatusResult> EvaluateWithdrawalAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StageGateStatusResult(true, "withdrawal"));
    }

    private sealed class Gfv : IGoodFaithViolationController
    {
        public List<string?> Codes { get; } = [];

        public Task<GoodFaithViolationClearResult> ClearAsync(string reason, CancellationToken cancellationToken = default) =>
            ClearAsync(reason, null, cancellationToken);

        public Task<GoodFaithViolationClearResult> ClearAsync(string reason, string? secondFactorCode, CancellationToken cancellationToken = default)
        {
            Codes.Add(secondFactorCode);
            return Task.FromResult(new GoodFaithViolationClearResult(true, true, "解除しました"));
        }
    }

    private sealed class Drift : IPositionDriftAdoptionController
    {
        public List<string?> Codes { get; } = [];

        public Task<PositionDriftAdoptionResult> AdoptAsync(
            string symbol, Market market, string reason, string onBehalfOf, CancellationToken cancellationToken = default) =>
            AdoptAsync(symbol, market, reason, onBehalfOf, null, cancellationToken);

        public Task<PositionDriftAdoptionResult> AdoptAsync(
            string symbol, Market market, string reason, string onBehalfOf, string? secondFactorCode, CancellationToken cancellationToken = default)
        {
            Codes.Add(secondFactorCode);
            return Task.FromResult(new PositionDriftAdoptionResult(true, true, "取り込みました"));
        }
    }

    private sealed class Reports : IReportReviewController
    {
        public List<(string PeriodKey, int Version, string? Code)> Confirms { get; } = [];

        public Task<ReportReviewResult> GetReviewAsync(string periodKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReportReviewResult(true, 3, "review"));

        public Task<ReportConfirmResult> ConfirmAsync(string periodKey, int expectedVersion, string onBehalfOf, CancellationToken cancellationToken = default) =>
            ConfirmAsync(periodKey, expectedVersion, onBehalfOf, null, cancellationToken);

        public Task<ReportConfirmResult> ConfirmAsync(
            string periodKey, int expectedVersion, string onBehalfOf, string? secondFactorCode, CancellationToken cancellationToken = default)
        {
            Confirms.Add((periodKey, expectedVersion, secondFactorCode));
            return Task.FromResult(new ReportConfirmResult(true, true, "確定しました"));
        }

        public Task<ReportReviewResult> RequestChangesAsync(string periodKey, int expectedVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReportReviewResult(true, expectedVersion + 1, "差し戻しました"));

        public Task<IReadOnlyList<string>> ListPeriodKeysAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    // ---- 再開・緩める 7 操作: コードが口へそのまま届き、ログに出ない ----

    [Fact]
    public async Task kill_switch_の解除はコードを運び_起動は運ばない()
    {
        var controller = new KillSwitch();
        var logger = new RecordingLogger<KillSwitchCommandHandler>();
        var handler = new KillSwitchCommandHandler(controller, Options(), logger);

        await handler.HandleAsync(Context("/killswitch off"), Phrase, Code);
        await handler.HandleAsync(Context("/killswitch"), Phrase, Code);

        controller.Calls.Should().Equal(("disengage", Code), ("engage", null));
        logger.Lines.Should().NotContain(l => l.Contains(Code));
    }

    [Fact]
    public async Task 再開はコードを運び_一時停止は運ばない()
    {
        var controller = new Pause();
        var logger = new RecordingLogger<PauseCommandHandler>();
        var handler = new PauseCommandHandler(controller, Options(), logger);

        await handler.HandleAsync(Context("/resume"), Code);
        await handler.HandleAsync(Context("/pause"), Code);

        controller.Calls.Should().Equal(("resume", Code), ("pause", null));
        logger.Lines.Should().NotContain(l => l.Contains(Code));
    }

    [Fact]
    public async Task 昇格はコードを運び_差し戻しは運ばない()
    {
        var controller = new Stage();
        var logger = new RecordingLogger<StageGateCommandHandler>();
        var handler = new StageGateCommandHandler(controller, Options(), logger);

        await handler.HandleAsync(Context("/stage promote 2"), Code);
        await handler.HandleAsync(Context("/stage demote 1"), Code);

        controller.Calls.Should().Equal((2, Code), (1, null));
        logger.Lines.Should().NotContain(l => l.Contains(Code));
    }

    [Fact]
    public async Task GFV_停止の解除はコードを運ぶ()
    {
        var controller = new Gfv();
        var logger = new RecordingLogger<GoodFaithViolationCommandHandler>();

        await new GoodFaithViolationCommandHandler(controller, Options(), logger)
            .HandleAsync(Context("/gfv clear"), Phrase, "原因を是正した", Code);

        controller.Codes.Should().Equal(Code);
        logger.Lines.Should().NotContain(l => l.Contains(Code));
    }

    [Fact]
    public async Task 乖離の取り込みはコードを運ぶ()
    {
        var controller = new Drift();
        var logger = new RecordingLogger<PositionDriftAdoptionCommandHandler>();

        await new PositionDriftAdoptionCommandHandler(controller, Options(), logger)
            .HandleAsync(Context("/drift adopt AAPL us"), Phrase, "証券会社のアプリで売却した", Code);

        controller.Codes.Should().Equal(Code);
        logger.Lines.Should().NotContain(l => l.Contains(Code));
    }

    [Fact]
    public async Task 報告書の確定はコードを運ぶ()
    {
        var controller = new Reports();
        var logger = new RecordingLogger<ReportCommandHandler>();

        await new ReportCommandHandler(controller, new VersionedConfirmationGuard(), Options(), logger)
            .HandleAsync(Context("/report approve daily-2026-09-01 3"), Code);

        controller.Confirms.Should().Equal(("daily-2026-09-01", 3, Code));
        logger.Lines.Should().NotContain(l => l.Contains(Code));
    }

    [Fact]
    public async Task 方針の確定はコードを報告書の確定の口へ運ぶ()
    {
        var controller = new Reports();
        var logger = new RecordingLogger<PolicyApprovalCommandHandler>();
        var reportLogger = new RecordingLogger<ReportCommandHandler>();
        var reportHandler = new ReportCommandHandler(controller, new VersionedConfirmationGuard(), Options(), reportLogger);

        await new PolicyApprovalCommandHandler(reportHandler, new FakePolicyRevisionController(), new FakeWatchlistController(), Options(), logger)
            .HandleAsync(Context("/policy approve daily-2026-09-01 4"), Code);

        controller.Confirms.Should().Equal(("daily-2026-09-01", 4, Code));
        logger.Lines.Concat(reportLogger.Lines).Should().NotContain(l => l.Contains(Code));
    }

    // ---- 入れなければ従来どおり（コードなし） ----

    [Fact]
    public async Task コードを入れなければ_null_を運び操作は従来どおり行う()
    {
        var killSwitch = new KillSwitch();
        var pause = new Pause();
        var stage = new Stage();
        var reports = new Reports();

        (await new KillSwitchCommandHandler(killSwitch, Options(), new RecordingLogger<KillSwitchCommandHandler>())
            .HandleAsync(Context("/killswitch off"), Phrase)).WasExecuted.Should().BeTrue();
        (await new PauseCommandHandler(pause, Options(), new RecordingLogger<PauseCommandHandler>())
            .HandleAsync(Context("/resume"))).WasExecuted.Should().BeTrue();
        (await new StageGateCommandHandler(stage, Options(), new RecordingLogger<StageGateCommandHandler>())
            .HandleAsync(Context("/stage promote 2"))).WasExecuted.Should().BeTrue();
        await new ReportCommandHandler(reports, new VersionedConfirmationGuard(), Options(), new RecordingLogger<ReportCommandHandler>())
            .HandleAsync(Context("/report approve daily-2026-09-01 3"));

        killSwitch.Calls.Should().Equal(("disengage", null));
        pause.Calls.Should().Equal(("resume", null));
        stage.Calls.Should().Equal((2, null));
        reports.Confirms.Should().Equal(("daily-2026-09-01", 3, null));
    }

    // ---- 閂で止まったら提供側を呼ばない（コードも送らない） ----

    [Fact]
    public async Task 閂で止まったときは提供側を呼ばずコードも送らない()
    {
        var killSwitch = new KillSwitch();
        var gfv = new Gfv();
        var drift = new Drift();
        var stage = new Stage();

        // 許可外の利用者。
        await new KillSwitchCommandHandler(killSwitch, Options(), new RecordingLogger<KillSwitchCommandHandler>())
            .HandleAsync(Context("/killswitch off", user: "someone-else"), Phrase, Code);
        await new StageGateCommandHandler(stage, Options(), new RecordingLogger<StageGateCommandHandler>())
            .HandleAsync(Context("/stage promote 2", user: "someone-else"), Code);
        // 確認フレーズの不一致。
        await new GoodFaithViolationCommandHandler(gfv, Options(), new RecordingLogger<GoodFaithViolationCommandHandler>())
            .HandleAsync(Context("/gfv clear"), "wrong", "原因を是正した", Code);
        // 理由の欠如。
        await new PositionDriftAdoptionCommandHandler(drift, Options(), new RecordingLogger<PositionDriftAdoptionCommandHandler>())
            .HandleAsync(Context("/drift adopt AAPL us"), Phrase, " ", Code);

        killSwitch.Calls.Should().BeEmpty();
        stage.Calls.Should().BeEmpty();
        gfv.Codes.Should().BeEmpty();
        drift.Codes.Should().BeEmpty();
    }
}
