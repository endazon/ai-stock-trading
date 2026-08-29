using NotificationService.Features.Notifications;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NotificationService.Tests;

// FR-14, UC-06, #341, IADR-0242 決定3: **設定値の変更は Discord からは参照のみである**ことの否定形。
//
// 計画（FR-14 / 詳細設計07 §コマンド体系）は
// 「設定値の変更（リスク上限・監視銘柄・取引ガード）は Discord からは**参照のみ**とし、変更は基盤チャットUI/
//  設定画面に限定する（誤操作・なりすまし時の被害限定のため。**kill switch/pause のみ例外**）」と定める。
//
// 🔴 **実装は満たしていたが、それを固定するテストが 1 つも無かった**（#341 のギャップ分析で実測）。
// 将来 `/config` を足しても CI は緑のままだったため、本ファイルで固定する。
public class DiscordSettingsAreReadOnlyTests
{
    private const string Guild = "guild-1";
    private const string Channel = "channel-1";
    private const string OwnerUser = "discord-owner-1";

    // 現実に打たれ得る「設定を変えようとするコマンド」。**1 つ残らず Unknown へ倒れること**を要求する。
    // 誤って解釈されれば、その語をハンドラが実行してしまう経路が生まれる。
    public static TheoryData<string> SettingChangeAttempts() =>
    [
        "/config set max-daily-order 100000",
        "/config",
        "/set risk-limit 50000",
        "/settings",
        "/setting update",
        "/watchlist add 7203",
        "/watchlist remove AAPL",
        "/assumptions set version 3",
        "/limit daily 100000",
        "/risk set max-drawdown 0.1",
        "/guard disable",
        "/stage set 3",
        "/killswitch phrase change",
        "/pause forever",
        "/resume all",
        "/report approve-all",
    ];

    [Theory]
    [MemberData(nameof(SettingChangeAttempts))]
    public void 設定を変更しようとするコマンドは解釈されない(string raw)
    {
        // FR-14: 参照のみ。解析の段階で Unknown に倒れ、いずれのハンドラも実行しない。
        BotCommandParser.Parse(raw).Kind.Should().Be(BotCommandKind.Unknown);
    }

    [Theory]
    [MemberData(nameof(SettingChangeAttempts))]
    public async Task 設定を変更しようとするコマンドではどのコントローラも呼ばれない(string raw)
    {
        // 解析だけでなく、**すべてのコマンドハンドラ**が実行しないことを確かめる（層を跨いだ否定形）。
        var probes = new Probes();
        var options = FullyConfigured();
        var context = new DiscordCommandContext(Guild, Channel, OwnerUser, IsDirectMessage: false, raw);

        var killSwitch = await new KillSwitchCommandHandler(
            probes.KillSwitch, options, NullLogger<KillSwitchCommandHandler>.Instance)
            .HandleAsync(context, "STOP TRADING");
        var pause = await new PauseCommandHandler(
            probes.Pause, options, NullLogger<PauseCommandHandler>.Instance).HandleAsync(context);
        var stage = await new StageGateCommandHandler(
            probes.StageGate, options, NullLogger<StageGateCommandHandler>.Instance).HandleAsync(context);
        var gfv = await new GoodFaithViolationCommandHandler(
            probes.Gfv, options, NullLogger<GoodFaithViolationCommandHandler>.Instance)
            .HandleAsync(context, "STOP TRADING", "理由");
        var report = await new ReportCommandHandler(
            probes.Report, new VersionedConfirmationGuard(), options,
            NullLogger<ReportCommandHandler>.Instance).HandleAsync(context);

        killSwitch.WasExecuted.Should().BeFalse();
        pause.WasExecuted.Should().BeFalse();
        stage.WasExecuted.Should().BeFalse();
        gfv.WasExecuted.Should().BeFalse();
        report.WasExecuted.Should().BeFalse();
        probes.Calls.Should().Be(0, "設定変更の試みでは、どの下流サービスも呼ばれてはならない");
    }

    [Theory]
    // 🔴 **例外はこの 2 系統だけである**（FR-14「kill switch と一時停止/再開のみを例外とする」）。
    // 例外の範囲が広がれば（＝新しい破壊的コマンドが解釈されるようになれば）本テストが落ちる。
    [InlineData("/killswitch", BotCommandKind.KillSwitchEngage)]
    [InlineData("/killswitch off", BotCommandKind.KillSwitchDisengage)]
    [InlineData("/pause", BotCommandKind.Pause)]
    [InlineData("/resume", BotCommandKind.Resume)]
    public void 参照のみの例外は_kill_switch_と一時停止_再開だけである(string raw, BotCommandKind expected)
    {
        BotCommandParser.Parse(raw).Kind.Should().Be(expected);
    }

    [Theory]
    // 参照系（副作用なし）は解釈してよい。**設定の「参照」は許される**のが FR-14 の定めである。
    [InlineData("/status", BotCommandKind.Status)]
    [InlineData("/stage status", BotCommandKind.StageStatus)]
    [InlineData("/report show daily-2026-08-28", BotCommandKind.ReportShow)]
    public void 参照系は解釈される(string raw, BotCommandKind expected)
    {
        BotCommandParser.Parse(raw).Kind.Should().Be(expected);
    }

    private static DiscordBotOptions FullyConfigured()
    {
        var options = new DiscordBotOptions
        {
            GuildId = Guild,
            ChannelId = Channel,
            KillSwitchConfirmationPhrase = "STOP TRADING",
        };
        options.AllowedUserIds.Add(OwnerUser);
        options.UserMapping[OwnerUser] = "endazon";
        return options;
    }

    // すべてのポートを 1 つのカウンタで束ねる。**1 回でも呼ばれたら失格**である。
    private sealed class Probes
    {
        public int Calls { get; private set; }

        public IKillSwitchController KillSwitch => new KillSwitchProbe(this);

        public IPauseController Pause => new PauseProbe(this);

        public IStageGateController StageGate => new StageGateProbe(this);

        public IGoodFaithViolationController Gfv => new GfvProbe(this);

        public IReportReviewController Report => new ReportProbe(this);

        private void Record() => Calls++;

        private sealed class KillSwitchProbe(Probes owner) : IKillSwitchController
        {
            public Task<KillSwitchResult> EngageAsync(string reason, CancellationToken cancellationToken = default)
            {
                owner.Record();
                return Task.FromResult(new KillSwitchResult(true, true, "起動"));
            }

            public Task<KillSwitchResult> DisengageAsync(string reason, CancellationToken cancellationToken = default)
            {
                owner.Record();
                return Task.FromResult(new KillSwitchResult(true, false, "解除"));
            }
        }

        private sealed class PauseProbe(Probes owner) : IPauseController
        {
            public Task<PauseResult> PauseAsync(string reason, CancellationToken cancellationToken = default)
            {
                owner.Record();
                return Task.FromResult(new PauseResult(true, true, "一時停止"));
            }

            public Task<PauseResult> ResumeAsync(string reason, CancellationToken cancellationToken = default)
            {
                owner.Record();
                return Task.FromResult(new PauseResult(true, false, "再開"));
            }

            public Task<RiskStatusResult> GetStatusAsync(CancellationToken cancellationToken = default)
            {
                owner.Record();
                return Task.FromResult(new RiskStatusResult(true, "稼働状態"));
            }
        }

        private sealed class StageGateProbe(Probes owner) : IStageGateController
        {
            public Task<StageGateStatusResult> GetStatusAsync(CancellationToken cancellationToken = default)
            {
                owner.Record();
                return Task.FromResult(new StageGateStatusResult(true, "段階ゲート"));
            }

            public Task<StageTransitionCommandResult> RequestTransitionAsync(
                int targetStage, CancellationToken cancellationToken = default)
            {
                owner.Record();
                return Task.FromResult(new StageTransitionCommandResult(true, true, "遷移"));
            }

            public Task<StageGateStatusResult> EvaluateWithdrawalAsync(CancellationToken cancellationToken = default)
            {
                owner.Record();
                return Task.FromResult(new StageGateStatusResult(true, "撤退評価"));
            }
        }

        private sealed class GfvProbe(Probes owner) : IGoodFaithViolationController
        {
            public Task<GoodFaithViolationClearResult> ClearAsync(
                string reason, CancellationToken cancellationToken = default)
            {
                owner.Record();
                return Task.FromResult(new GoodFaithViolationClearResult(true, true, "解除"));
            }
        }

        private sealed class ReportProbe(Probes owner) : IReportReviewController
        {
            public Task<ReportReviewResult> GetReviewAsync(
                string periodKey, CancellationToken cancellationToken = default)
            {
                owner.Record();
                return Task.FromResult(new ReportReviewResult(true, 1, "版 1"));
            }

            public Task<ReportConfirmResult> ConfirmAsync(
                string periodKey, int expectedVersion, CancellationToken cancellationToken = default)
            {
                owner.Record();
                return Task.FromResult(new ReportConfirmResult(true, true, "確定"));
            }

            public Task<ReportReviewResult> RequestChangesAsync(
                string periodKey, int expectedVersion, CancellationToken cancellationToken = default)
            {
                owner.Record();
                return Task.FromResult(new ReportReviewResult(true, 1, "差し戻し"));
            }
        }
    }
}
