using NotificationService.Domain;
using NotificationService.Features.Notifications;
using NotificationService.Features.Notifications.AdoptPositionDrift;
using NotificationService.Features.Notifications.ClearGoodFaithViolations;
using NotificationService.Features.Notifications.OperateKillSwitch;
using NotificationService.Features.Notifications.OperateStageGate;
using NotificationService.Features.Notifications.OperateTradingPause;
using NotificationService.Features.Notifications.ReviewReport;
using NotificationService.Features.Notifications.RevisePolicy;
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
        // FR-13, ADR-0042 決定 2, #1025: `/policy approve` は**銘柄を取らない**。銘柄を添えた形・版の無い形は解釈されない。
        "/policy approve daily-2026-09-28 3 NVDA",
        "/policy approve daily-2026-09-28",
        "/policy add NVDA",
        "/policy watchlist add NVDA",
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
        // #871, IADR-0423: 乖離の取り込み（台帳の是正）も、設定変更の試みでは起動しない。
        var drift = await new PositionDriftAdoptionCommandHandler(
            probes.Drift, options, NullLogger<PositionDriftAdoptionCommandHandler>.Instance)
            .HandleAsync(context, "STOP TRADING", "理由");
        // T-10-1334, #1016, IADR-0431: 方針の改訂も、設定変更の試みでは起動しない（指示の本文に同じ語を入れても同じ）。
        var policy = await new PolicyRevisionCommandHandler(
            probes.Policy, probes.Watchlist, options, NullLogger<PolicyRevisionCommandHandler>.Instance)
            .HandleAsync(context, raw);
        // FR-13, ADR-0042 決定 1・2, #1025: 入れ替え案の適用（唯一の例外）も、設定変更の試みでは起動しない。
        var approval = await new PolicyApprovalCommandHandler(
            new ReportCommandHandler(probes.Report, new VersionedConfirmationGuard(), options, NullLogger<ReportCommandHandler>.Instance),
            probes.Policy, probes.Watchlist, options, NullLogger<PolicyApprovalCommandHandler>.Instance)
            .HandleAsync(context);

        killSwitch.WasExecuted.Should().BeFalse();
        pause.WasExecuted.Should().BeFalse();
        stage.WasExecuted.Should().BeFalse();
        gfv.WasExecuted.Should().BeFalse();
        report.WasExecuted.Should().BeFalse();
        drift.WasExecuted.Should().BeFalse();
        policy.WasExecuted.Should().BeFalse();
        approval.ConfirmedNow.Should().BeFalse();
        approval.WatchlistApplyStatus.Should().BeNull();
        probes.Calls.Should().Be(0, "設定変更の試みでは、どの下流サービスも呼ばれてはならない");
    }

    [Theory]
    // 🔴 **例外はこの 3 系統だけである**（FR-14「kill switch と一時停止/再開のみを例外とする」＋計画 ADR-0042 決定 2
    // 「決定 1 の入れ替え案の適用だけを足す」）。例外の範囲が広がれば（＝新しい破壊的コマンドが解釈されるようになれば）本テストが落ちる。
    [InlineData("/killswitch", BotCommandKind.KillSwitchEngage)]
    [InlineData("/killswitch off", BotCommandKind.KillSwitchDisengage)]
    [InlineData("/pause", BotCommandKind.Pause)]
    [InlineData("/resume", BotCommandKind.Resume)]
    // T-10-1362, FR-13, ADR-0042 決定 1・2, #1025: 利用者が確定した `/policy` の案の入れ替えの適用（銘柄は取らない）。
    [InlineData("/policy approve daily-2026-09-28 3", BotCommandKind.PolicyApprove)]
    public void 参照のみの例外は_kill_switch_と一時停止_再開と方針案の入れ替えの適用だけである(string raw, BotCommandKind expected)
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

    // T-10-1335（#1025 で改訂）, FR-14, ADR-0042 決定 1・2, IADR-0433: 監視銘柄を変え得る口は**市場監視の 1 つのポートだけ**で、
    // 持つのは「現在の監視銘柄の照会」と「案の適用」の 2 つに限る（銘柄を自由に追加・削除する口は無い）。
    // その口を持つハンドラは `/policy` の 2 つだけ（照会＝案の土台、適用＝確定した案）。口やハンドラが増えれば本テストが落ちる
    // ——FR-14 の例外を広げるには計画の改定が要る。
    [Fact]
    public void 監視銘柄を変え得る口は案の適用だけで_持つのは方針の改訂のハンドラだけである()
    {
        var assembly = typeof(PolicyRevisionCommandHandler).Assembly;

        assembly.GetTypes().Where(t => t.IsInterface && t.Name.Contains("Watchlist", StringComparison.OrdinalIgnoreCase))
            .Should().Equal(typeof(IMarketMonitorWatchlistController));
        typeof(IMarketMonitorWatchlistController).GetMethods().Select(m => m.Name).Order()
            .Should().Equal(nameof(IMarketMonitorWatchlistController.ApplyProposalAsync), nameof(IMarketMonitorWatchlistController.GetWatchlistAsync));
        typeof(IPolicyRevisionController).GetMethods().Select(m => m.Name).Order()
            .Should().Equal(
                nameof(IPolicyRevisionController.GetWatchlistProposalAsync),
                nameof(IPolicyRevisionController.RecordWatchlistApplyAsync),
                nameof(IPolicyRevisionController.ReviseAsync));

        var holders = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Namespace?.StartsWith("NotificationService.Features", StringComparison.Ordinal) == true)
            .Where(t => t.GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IMarketMonitorWatchlistController))))
            .Select(t => t.Name)
            .Order()
            .ToList();
        holders.Should().Equal(nameof(PolicyApprovalCommandHandler), nameof(PolicyRevisionCommandHandler));
    }

    // T-10-1423（PR #1027 の監査 Info）: 名前だけでなく**文字列**でも固定する——市場監視の監視銘柄の API のパス
    // （`/monitor/watchlist`）を持つのはアダプタ HttpMarketMonitorWatchlistController だけ、名前付き HttpClient
    // `"market-monitor-watchlist"` を扱うのは組み立て（Program.cs）だけ。別の型が同じ API を直接叩くようになれば赤。
    [Fact]
    public void 監視銘柄のAPIのパスと名前付きクライアントは決まった場所にしか無い()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NotificationService.csproj")))
            dir = dir.Parent;
        dir.Should().NotBeNull("NotificationService.csproj の場所が見つからない");
        var sources = Directory.EnumerateFiles(dir!.FullName, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        sources.Count.Should().BeGreaterThan(40, "母集合を読めている");

        sources.Where(f => File.ReadAllText(f).Contains("/monitor/watchlist", StringComparison.Ordinal)).Select(Path.GetFileName)
            .Should().Equal("HttpMarketMonitorWatchlistController.cs");
        sources.Where(f => File.ReadAllText(f).Contains("\"market-monitor-watchlist\"", StringComparison.Ordinal)).Select(Path.GetFileName)
            .Should().Equal("Program.cs");
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

        public IPositionDriftAdoptionController Drift => new DriftProbe(this);

        public IPolicyRevisionController Policy => new PolicyProbe(this);

        public IMarketMonitorWatchlistController Watchlist => new WatchlistProbe(this);

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
                int targetStage, string onBehalfOf, CancellationToken cancellationToken = default)
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

        private sealed class DriftProbe(Probes owner) : IPositionDriftAdoptionController
        {
            public Task<PositionDriftAdoptionResult> AdoptAsync(
                string symbol, AiStockTrading.Shared.Contracts.Trading.Market market, string reason, string onBehalfOf,
                CancellationToken cancellationToken = default)
            {
                owner.Record();
                return Task.FromResult(new PositionDriftAdoptionResult(true, true, "取り込み"));
            }
        }

        private sealed class PolicyProbe(Probes owner) : IPolicyRevisionController
        {
            public Task<PolicyRevisionCommandOutcome> ReviseAsync(
                string? periodKey, string instruction, string onBehalfOf, IReadOnlyList<WatchlistSnapshotItemView>? currentWatchlist,
                CancellationToken cancellationToken = default)
            {
                owner.Record();
                return Task.FromResult(new PolicyRevisionCommandOutcome(true, false, "改訂"));
            }

            public Task<WatchlistProposalLookup> GetWatchlistProposalAsync(
                string periodKey, int version, CancellationToken cancellationToken = default)
            {
                owner.Record();
                return Task.FromResult(new WatchlistProposalLookup(true, false, null, "案なし"));
            }

            public Task<bool> RecordWatchlistApplyAsync(
                Guid attemptId, string outcome, IReadOnlyList<WatchlistApplyItemView> items, string message, string onBehalfOf,
                CancellationToken cancellationToken = default)
            {
                owner.Record();
                return Task.FromResult(true);
            }
        }

        private sealed class WatchlistProbe(Probes owner) : IMarketMonitorWatchlistController
        {
            public Task<WatchlistSnapshotResult> GetWatchlistAsync(CancellationToken cancellationToken = default)
            {
                owner.Record();
                return Task.FromResult(new WatchlistSnapshotResult(true, [], "照会"));
            }

            public Task<WatchlistApplyOutcome> ApplyProposalAsync(
                IReadOnlyList<WatchlistSnapshotItemView> expected, IReadOnlyList<WatchlistChangeSuggestionView> changes,
                string proposalRef, string onBehalfOf, CancellationToken cancellationToken = default)
            {
                owner.Record();
                return Task.FromResult(new WatchlistApplyOutcome(WatchlistApplyStatus.Applied, [], null, "適用"));
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
                string periodKey, int expectedVersion, string onBehalfOf, CancellationToken cancellationToken = default)
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

            // #834: 入力補完の一覧照会も「コントローラを呼んだ」として数える（設定変更系では呼ばれない）。
            public Task<IReadOnlyList<string>> ListPeriodKeysAsync(CancellationToken cancellationToken = default)
            {
                owner.Record();
                return Task.FromResult<IReadOnlyList<string>>([]);
            }
        }
    }
}
