extern alias MarketMonitorWorker;
extern alias ReportWorker;
extern alias RiskManagementWorker;

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Grpc;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationService.Domain;
using NotificationService.Features.Notifications;
using NotificationService.Infrastructure.ExternalServices;
using Xunit;
using MonitorDomain = MarketMonitorWorker::MarketMonitorService.Domain;
using MonitorFeatures = MarketMonitorWorker::MarketMonitorService.Features.MarketMonitor;
using ReportDomain = ReportWorker::ReportService.Domain;
using ReportFeatures = ReportWorker::ReportService.Features.Reports;
using ReportProposal = ReportWorker::ReportService.Features.Reports.WatchlistProposal;
using ReportRevise = ReportWorker::ReportService.Features.Reports.RevisePolicy;
using ReportProto = AiStockTrading.Shared.Grpc.Report.V1;
using RiskDomain = RiskManagementWorker::RiskManagementService.Domain;
using RiskFeatures = RiskManagementWorker::RiskManagementService.Features.RiskManagement;
using RiskProto = AiStockTrading.Shared.Grpc.RiskManagement.V1;
using RiskStatus = RiskManagementWorker::RiskManagementService.Features.RiskManagement.GetRiskStatus;

namespace NotificationService.Tests;

// T-10-1730, NFR, FR-14, FR-07, FR-10, FR-13, FR-20, MSP:ADR-0029, ADR-0047 決定 1・2, IADR-0284 決定 5（段 5）, IADR-0449 決定 3・4, #753:
// Discord ボットの読み取り 6 本の **gRPC 実装が REST 実装と同じ結果**を返すこと。
//
// 🔴 観測の仕方（パリティ）: **送り手の本物の値**を 1 つ作り、
//   - REST 側は送り手の JSON 設定で直列化した応答を HttpClient の偽のハンドラから返し、`Http*` アダプタで読む、
//   - gRPC 側は**送り手の本物の写し**（`RiskReadWireMapping` / `ReportOwnerReadWireMapping` / `WatchlistWireMapping`）で proto にしたものを
//     実 Kestrel の h2c の偽の提供側（127.0.0.1）から返し、`Grpc*` 実装で読む、
// その 2 つの結果を**等価比較**する。写しの取り違え・解釈の分岐（片方だけ直った規則）があれば赤になる。
// あわせて、失敗が成功に見えないこと・REST の同じ失敗と同じ文言になることを固定する。
// （前半では「書き込みは REST へ委ねる」もここで固定していた。段 5 の後半で書き込みも gRPC へ移したので、書き込みの観点は
// T-10-1735（GrpcBotWritesTests）へ移した＝反転した。IADR-0450。）
public class GrpcBotReadsTests
{
    private const string Token = "bot-owner-token";
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions RiskWire = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions ReportWire =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly JsonSerializerOptions MonitorWire = new(JsonSerializerDefaults.Web);

    // ---- 組み立て ----

    private sealed class FixedToken(string? token) : IServiceAccessTokenProvider
    {
        public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(token);
    }

    private static Grpc.Net.Client.GrpcChannel Channel(BotReadGrpcStubHost host, string? token = Token) =>
        GrpcClientExtensions.CreateAiStockTradingChannel(host.Address, new FixedToken(token));

    private static RiskManagementGrpcTransport Risk(BotReadGrpcStubHost host, TimeSpan? timeout = null, string? token = Token) =>
        new(Channel(host, token), timeout ?? TimeSpan.FromSeconds(5), 1, NullLogger<RiskManagementGrpcTransport>.Instance);

    private static ReportsGrpcTransport Reports(BotReadGrpcStubHost host, TimeSpan? timeout = null) =>
        new(Channel(host), timeout ?? TimeSpan.FromSeconds(5), 1, NullLogger<ReportsGrpcTransport>.Instance);

    private static MarketMonitorGrpcTransport Monitor(BotReadGrpcStubHost host, int attempts = 1) =>
        new(Channel(host), TimeSpan.FromSeconds(5), attempts, NullLogger<MarketMonitorGrpcTransport>.Instance);

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private static HttpClient RestReturning(object? value, JsonSerializerOptions options, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new StubHandler(status, value is null ? "{}" : JsonSerializer.Serialize(value, value.GetType(), options)))
        {
            BaseAddress = new Uri("http://sender"),
        };

    // ---- 送り手の本物の値 ----

    private static RiskStatus.RiskStatusView SentStatus(decimal? maxDaily, DateOnly? releaseOn) => new(
        false, releaseOn is not null, releaseOn, true, RiskStatus.ActiveTradingControl.Pause, true, TradingStage.Stage1Simulate,
        BrokerProvider.MoomooSimulate, 1200m, -350.5m, 849.5m, 1_000_000m, 30000m, 100000m, maxDaily, 0.031m, 0.2m, 2, 5);

    private static RiskFeatures.StageGateStatus StageGate(RiskDomain.Stage1GateCriteria criteria, bool withdrawal) => new(
        TradingStage.Stage2MinimalLive,
        new RiskDomain.StageSettings(TradingStage.Stage2MinimalLive, BrokerProvider.MoomooReal, 0.30m),
        [
            new RiskDomain.StageTransition(1, TradingStage.Stage0Verification, TradingStage.Stage1Simulate, RiskDomain.StageTransitionKind.Promotion, "owner", T0, "昇格"),
            new RiskDomain.StageTransition(2, TradingStage.Stage1Simulate, TradingStage.Stage2MinimalLive, RiskDomain.StageTransitionKind.Promotion, "owner", T0.AddDays(70), "昇格"),
            new RiskDomain.StageTransition(3, TradingStage.Stage2MinimalLive, TradingStage.Stage2MinimalLive, RiskDomain.StageTransitionKind.ShortSellReleaseVerdict, "owner", T0.AddDays(80), "verdict"),
        ],
        new RiskDomain.PromotionAssessment(
            TradingStage.Stage3ScaledLive, false,
            [RiskDomain.StageGateCriterion.SlippageOrCostExceeded, RiskDomain.StageGateCriterion.ControlViolationCountUnavailable]),
        withdrawal
            ? new RiskDomain.WithdrawalAssessment(true, RiskDomain.WithdrawalReason.DrawdownBreachedMultiple, true, TradingStage.Stage0Verification)
            : new RiskDomain.WithdrawalAssessment(false, null, false, null),
        new RiskDomain.Stage1Progress(60, 120),
        criteria,
        new RiskFeatures.ShortSellReleaseState(RiskDomain.ShortSellReleaseVerdictStatus.Missing, null, "fp", "strategy", false, null, 0));

    // ---- パリティ（REST と gRPC で同じ結果） ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T_10_1730_稼働状態は_REST_と同じ結果(bool resolved)
    {
        var sent = SentStatus(resolved ? 300000m : null, resolved ? new DateOnly(2026, 9, 2) : null);
        var behavior = new BotReadStubBehavior { RiskStatus = BotReadStubBehavior.Returns(RiskFeatures.RiskReadWireMapping.ToProto(sent)) };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        using var transport = Risk(host);
        var restController = new HttpPauseController(RestReturning(sent, RiskWire), NullLogger<HttpPauseController>.Instance);
        var grpcController = new GrpcPauseController(transport, NullLogger<GrpcPauseController>.Instance);

        var rest = await restController.GetStatusAsync();
        var grpc = await grpcController.GetStatusAsync();

        rest.Succeeded.Should().BeTrue();
        grpc.Should().Be(rest);
        grpc.Message.Contains(HttpPauseController.UnknownDailyOrderCap).Should().Be(!resolved, "上限が分からないことを 0 と表示しない（#990）");
        behavior.Received.Should().ContainSingle().Which.Should().Be(("GetRiskStatus", $"Bearer {Token}"),
            "メタデータにはボット自身の owner トークンを載せる（ADR-0047 決定 2）");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T_10_1730_段階ゲートの現況は_REST_と同じ結果と警告(bool lowered)
    {
        var sent = StageGate(lowered ? new RiskDomain.Stage1GateCriteria(60, 50, 120) : RiskDomain.Stage1GateCriteria.Default, withdrawal: lowered);
        var behavior = new BotReadStubBehavior { StageGate = BotReadStubBehavior.Returns(RiskFeatures.RiskReadWireMapping.ToProto(sent)) };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        using var transport = Risk(host);
        var restController = new HttpStageGateController(RestReturning(sent, RiskWire), NullLogger<HttpStageGateController>.Instance);
        var grpcController = new GrpcStageGateController(transport, NullLogger<GrpcStageGateController>.Instance);

        var rest = await restController.GetStatusAsync();
        var grpc = await grpcController.GetStatusAsync();

        rest.Succeeded.Should().BeTrue();
        grpc.Should().Be(rest);
        grpc.Message.Should().Contain("#3 不明(2)", "REST と同じく種別の序数で引く（verdict の既存の表示を変えない）")
            .And.Contain("moomoo REAL（実弾）").And.Contain("実効スリッページ・費用が想定超過");
        (grpc.Stage1Warning is not null).Should().Be(lowered);
        if (lowered)
            grpc.Message.Should().Contain("撤退基準に抵触（実DDがバックテスト最大DD×倍率に到達）").And.Contain("Stage 0（検証）");
    }

    [Fact]
    public async Task T_10_1730_レビュー局面は_REST_と同じ結果と未供給の注記()
    {
        var sent = new ReportFeatures.ReportReviewView("daily-2026-09-01", ReportDomain.ReviewState.Drafting, 3, ["建玉", "散文（LLM）"]);
        var behavior = new BotReadStubBehavior { Review = BotReadStubBehavior.Returns(ReportFeatures.ReportOwnerReadWireMapping.ToProto(sent)) };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        using var transport = Reports(host);
        var restController = new HttpReportReviewController(RestReturning(sent, ReportWire), NullLogger<HttpReportReviewController>.Instance);
        var grpcController = new GrpcReportReviewController(transport, NullLogger<GrpcReportReviewController>.Instance);

        var rest = await restController.GetReviewAsync("daily-2026-09-01");
        var grpc = await grpcController.GetReviewAsync("daily-2026-09-01");

        rest.Succeeded.Should().BeTrue();
        grpc.Should().Be(rest);
        grpc.Message.Should().Contain(ReportUnsuppliedNotice.Prefix).And.Contain("散文（LLM）");
    }

    [Fact]
    public async Task T_10_1730_会話キーの一覧は_REST_と同じ順()
    {
        ReportFeatures.ReportPeriodKeyItem[] sent =
        [
            new("daily-2026-09-17", new DateOnly(2026, 9, 17)),
            new("weekly-2026-W38", new DateOnly(2026, 9, 14)),
            new("daily-2026-09-18", new DateOnly(2026, 9, 18)),
            new("daily-2026-09-16", new DateOnly(2026, 9, 18)),
        ];
        var wire = new ReportProto.ListReportPeriodKeysResponse();
        wire.Items.AddRange(sent.Select(ReportFeatures.ReportOwnerReadWireMapping.ToProto));
        // 読めない開始日の 1 件（REST の「数値で来た 1 件」に相当）は末尾へ回る。空のキーは落とす。
        wire.Items.Add(new ReportProto.ReportPeriodKeyRow { PeriodKey = "monthly-2026-08", PeriodStart = "not-a-date" });
        wire.Items.Add(new ReportProto.ReportPeriodKeyRow { PeriodStart = "2026-09-30" });
        var behavior = new BotReadStubBehavior { PeriodKeys = BotReadStubBehavior.Returns(wire) };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        using var transport = Reports(host);
        var restController = new HttpReportReviewController(RestReturning(sent, ReportWire), NullLogger<HttpReportReviewController>.Instance);
        var grpcController = new GrpcReportReviewController(transport, NullLogger<GrpcReportReviewController>.Instance);

        var rest = await restController.ListPeriodKeysAsync();
        var grpc = await grpcController.ListPeriodKeysAsync();

        grpc.Take(4).Should().Equal(rest);
        grpc.Should().Equal("daily-2026-09-18", "daily-2026-09-16", "daily-2026-09-17", "weekly-2026-W38", "monthly-2026-08");
    }

    [Theory]
    [InlineData("有り")]
    [InlineData("不明")]
    public async Task T_10_1730_入れ替え案は_REST_と同じ結果(string snapshot)
    {
        var sent = new ReportProposal.WatchlistProposalView(
            Guid.NewGuid(), "daily-2026-09-01", 1,
            [new ReportRevise.WatchlistChangeView("add", "NVDA", "出来高"), new ReportRevise.WatchlistChangeView("remove", "7203", "")],
            snapshot == "有り" ? [new ReportProposal.WatchlistSnapshotEntryView("7203", "Japan")] : null,
            false);
        var behavior = new BotReadStubBehavior { Proposal = BotReadStubBehavior.Returns(ReportFeatures.ReportOwnerReadWireMapping.ToProto(sent)) };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        using var transport = Reports(host);
        var restController = new HttpPolicyRevisionController(RestReturning(sent, ReportWire), NullLogger<HttpPolicyRevisionController>.Instance);
        var grpcController = new GrpcPolicyRevisionController(transport, NullLogger<GrpcPolicyRevisionController>.Instance);

        var rest = await restController.GetWatchlistProposalAsync("daily-2026-09-01", 1);
        var grpc = await grpcController.GetWatchlistProposalAsync("daily-2026-09-01", 1);

        rest.Found.Should().BeTrue();
        grpc.Should().BeEquivalentTo(rest);
        (grpc.Proposal!.Snapshot is null).Should().Be(snapshot == "不明", "分からない（null）を空の一覧と取り違えない");
    }

    [Fact]
    public async Task T_10_1730_監視銘柄は_REST_と同じ結果で日本は未指定に化けない()
    {
        MonitorDomain.MonitoredSymbol[] sent = [new("AAPL", Market.UnitedStates), new("7203", Market.Japan)];
        var wire = new AiStockTrading.Shared.Grpc.MarketMonitor.V1.GetWatchlistResponse();
        wire.Items.AddRange(sent.Select(MonitorFeatures.WatchlistWireMapping.ToProto));
        var behavior = new BotReadStubBehavior { Watchlist = BotReadStubBehavior.Returns(wire) };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        using var transport = Monitor(host);
        var restController = new HttpMarketMonitorWatchlistController(
            RestReturning(sent, MonitorWire), NullLogger<HttpMarketMonitorWatchlistController>.Instance);
        var grpcController = new GrpcMarketMonitorWatchlistController(transport, NullLogger<GrpcMarketMonitorWatchlistController>.Instance);

        var rest = await restController.GetWatchlistAsync();
        var grpc = await grpcController.GetWatchlistAsync();

        rest.Succeeded.Should().BeTrue();
        grpc.Should().BeEquivalentTo(rest);
        grpc.Items.Should().Contain(new WatchlistSnapshotItemView("7203", "Japan"));
    }

    // ---- 失敗の写し（REST の同じ失敗と同じ種類。成功に見せない） ----

    [Fact]
    public async Task T_10_1730_対象が無い_案ではない_その版で確定されていないは_REST_と同じ文言()
    {
        var behavior = new BotReadStubBehavior
        {
            Review = BotReadStubBehavior.Fails<ReportProto.GetReportReviewResponse>(StatusCode.NotFound),
            Proposal = BotReadStubBehavior.Fails<ReportProto.GetWatchlistProposalResponse>(StatusCode.NotFound),
        };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        using var transport = Reports(host);
        var review = new GrpcReportReviewController(transport, NullLogger<GrpcReportReviewController>.Instance);
        var restReview = new HttpReportReviewController(RestReturning(null, ReportWire, HttpStatusCode.NotFound), NullLogger<HttpReportReviewController>.Instance);

        (await review.GetReviewAsync("daily-2099-01-01")).Should().Be(await restReview.GetReviewAsync("daily-2099-01-01"));

        var restNotProposal = await new HttpPolicyRevisionController(
            RestReturning(null, ReportWire, HttpStatusCode.NotFound), NullLogger<HttpPolicyRevisionController>.Instance).GetWatchlistProposalAsync("k", 1);
        var restNotConfirmed = await new HttpPolicyRevisionController(
            RestReturning(null, ReportWire, HttpStatusCode.Conflict), NullLogger<HttpPolicyRevisionController>.Instance).GetWatchlistProposalAsync("k", 1);
        var grpcPolicy = new GrpcPolicyRevisionController(transport, NullLogger<GrpcPolicyRevisionController>.Instance);

        (await grpcPolicy.GetWatchlistProposalAsync("k", 1)).Should().Be(restNotProposal);
        behavior.Proposal = BotReadStubBehavior.Fails<ReportProto.GetWatchlistProposalResponse>(StatusCode.FailedPrecondition);
        (await grpcPolicy.GetWatchlistProposalAsync("k", 1)).Should().Be(restNotConfirmed, "その版で確定されていない＝適用しない（監査 H1）");
    }

    [Theory]
    [InlineData(StatusCode.Unauthenticated, true)]
    [InlineData(StatusCode.PermissionDenied, true)]
    [InlineData(StatusCode.Unavailable, false)]
    [InlineData(StatusCode.Unimplemented, false)]   // 提供側が古い（配備順の窓）
    [InlineData(StatusCode.Internal, false)]
    public async Task T_10_1730_呼び出しの失敗は失敗として返し資格情報の失敗には注記を添える(StatusCode status, bool hint)
    {
        var behavior = new BotReadStubBehavior
        {
            RiskStatus = BotReadStubBehavior.Fails<RiskProto.GetRiskStatusResponse>(status),
            StageGate = BotReadStubBehavior.Fails<RiskProto.GetStageGateResponse>(status),
            Review = BotReadStubBehavior.Fails<ReportProto.GetReportReviewResponse>(status),
            PeriodKeys = BotReadStubBehavior.Fails<ReportProto.ListReportPeriodKeysResponse>(status),
            Proposal = BotReadStubBehavior.Fails<ReportProto.GetWatchlistProposalResponse>(status),
            Watchlist = BotReadStubBehavior.Fails<AiStockTrading.Shared.Grpc.MarketMonitor.V1.GetWatchlistResponse>(status),
        };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        using var risk = Risk(host);
        using var reports = Reports(host);
        using var monitor = Monitor(host);

        var statusResult = await new GrpcPauseController(risk, NullLogger<GrpcPauseController>.Instance).GetStatusAsync();
        var stage = await new GrpcStageGateController(risk, NullLogger<GrpcStageGateController>.Instance).GetStatusAsync();
        var reviewController = new GrpcReportReviewController(reports, NullLogger<GrpcReportReviewController>.Instance);
        var review = await reviewController.GetReviewAsync("daily-2026-09-01");
        var keys = await reviewController.ListPeriodKeysAsync();
        var proposal = await new GrpcPolicyRevisionController(reports, NullLogger<GrpcPolicyRevisionController>.Instance)
            .GetWatchlistProposalAsync("daily-2026-09-01", 1);
        var watchlist = await new GrpcMarketMonitorWatchlistController(monitor, NullLogger<GrpcMarketMonitorWatchlistController>.Instance).GetWatchlistAsync();

        (statusResult.Succeeded, stage.Succeeded, review.Succeeded, review.Version).Should().Be((false, false, false, 0));
        (proposal.Succeeded, proposal.Found, watchlist.Succeeded).Should().Be((false, false, false));
        watchlist.Items.Should().BeEmpty();
        keys.Should().BeEmpty("入力補完は REST と同じく失敗を空で返す（退避の全件照会は重ねない）");
        foreach (var message in new[] { statusResult.Message, stage.Message, review.Message })
        {
            message.Should().Contain(status.ToString());
            message.Contains(NotificationGrpcCalls.OwnerHint).Should().Be(hint);
        }
    }

    // 🔴 原則 A: 必須の項目の欠落を既定値（0・false・Stage 0）で読まない。
    [Fact]
    public async Task T_10_1730_必須の項目が欠けた応答は解釈できないとして返す()
    {
        var status = RiskFeatures.RiskReadWireMapping.ToProto(SentStatus(300000m, null));
        status.ClearTradingPaused();
        var stage = RiskFeatures.RiskReadWireMapping.ToProto(StageGate(RiskDomain.Stage1GateCriteria.Default, withdrawal: false));
        stage.CurrentStage = RiskProto.TradingStage.Unspecified;
        var behavior = new BotReadStubBehavior
        {
            RiskStatus = BotReadStubBehavior.Returns(status),
            StageGate = BotReadStubBehavior.Returns(stage),
            Review = BotReadStubBehavior.Returns(new ReportProto.GetReportReviewResponse()),
            Proposal = BotReadStubBehavior.Returns(new ReportProto.GetWatchlistProposalResponse { AttemptId = "not-a-guid", ReportVersion = 1, ApplyRecorded = false }),
            Watchlist = BotReadStubBehavior.Returns(new AiStockTrading.Shared.Grpc.MarketMonitor.V1.GetWatchlistResponse
            {
                Items = { new AiStockTrading.Shared.Grpc.MarketMonitor.V1.WatchlistItem { Symbol = "7203" } },  // 市場が未指定
            }),
        };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        using var risk = Risk(host);
        using var reports = Reports(host);
        using var monitor = Monitor(host);

        (await new GrpcPauseController(risk, NullLogger<GrpcPauseController>.Instance).GetStatusAsync())
            .Should().Be(new RiskStatusResult(false, "稼働状態の応答を解釈できませんでした"));
        (await new GrpcStageGateController(risk, NullLogger<GrpcStageGateController>.Instance).GetStatusAsync())
            .Should().Be(new StageGateStatusResult(false, "段階ゲートの応答を解釈できませんでした"));
        (await new GrpcReportReviewController(reports, NullLogger<GrpcReportReviewController>.Instance).GetReviewAsync("k"))
            .Should().Be(new ReportReviewResult(false, 0, "レビュー局面の応答を解釈できませんでした"), "版番号を騙らない");
        var proposal = await new GrpcPolicyRevisionController(reports, NullLogger<GrpcPolicyRevisionController>.Instance)
            .GetWatchlistProposalAsync("k", 1);
        (proposal.Succeeded, proposal.Found).Should().Be((false, false));
        (await new GrpcMarketMonitorWatchlistController(monitor, NullLogger<GrpcMarketMonitorWatchlistController>.Instance).GetWatchlistAsync()).Succeeded.Should().BeFalse();
    }

    // deadline: 試行ごとの上限で打ち切り、タイムアウトの文言を返す（per-call 1 秒・上限 15 秒＝10 倍以上）。
    [Fact]
    public async Task T_10_1730_応答が来なければ試行ごとの上限で打ち切りタイムアウトとして返す()
    {
        var behavior = new BotReadStubBehavior
        {
            RiskStatus = BotReadStubBehavior.Hangs<RiskProto.GetRiskStatusResponse>(),
            Review = BotReadStubBehavior.Hangs<ReportProto.GetReportReviewResponse>(),
        };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        using var risk = Risk(host, TimeSpan.FromSeconds(1));
        using var reports = Reports(host, TimeSpan.FromSeconds(1));

        var watch = Stopwatch.StartNew();
        var status = await new GrpcPauseController(risk, NullLogger<GrpcPauseController>.Instance).GetStatusAsync();
        var review = await new GrpcReportReviewController(reports, NullLogger<GrpcReportReviewController>.Instance).GetReviewAsync("k");
        watch.Stop();

        status.Should().Be(new RiskStatusResult(false, "稼働状態の照会がタイムアウトしました"));
        // NFR, IADR-0450, #753（PR #1069 の監査）: 読み取りの時間切れに「結果は不明」は付けない（状態を変えないので不明になる結果が無い）。
        review.Should().Be(new ReportReviewResult(false, 0, "レビュー局面の照会がタイムアウトしました"));
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    // 再試行は UNAVAILABLE / DEADLINE_EXCEEDED だけ（宣言した回数まで）。
    [Fact]
    public async Task T_10_1730_UNAVAILABLE_は宣言した回数まで再試行する()
    {
        var ok = new AiStockTrading.Shared.Grpc.MarketMonitor.V1.GetWatchlistResponse();
        var behavior = new BotReadStubBehavior
        {
            Watchlist = (call, _) => call == 1
                ? throw new RpcException(new Status(StatusCode.Unavailable, "down"))
                : Task.FromResult(ok),
        };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        using var monitor = Monitor(host, attempts: 2);

        (await new GrpcMarketMonitorWatchlistController(monitor, NullLogger<GrpcMarketMonitorWatchlistController>.Instance).GetWatchlistAsync()).Succeeded.Should().BeTrue();
        behavior.Calls("GetWatchlist").Should().Be(2);
    }

    // 🔴 資格情報が未構成（取得器が null を返す）ならメタデータを付けない（→ 提供側が UNAUTHENTICATED ＝ REST の 401 と同じ向き）。
    [Fact]
    public async Task T_10_1730_トークンが無ければメタデータを付けない()
    {
        var behavior = new BotReadStubBehavior { RiskStatus = BotReadStubBehavior.Fails<RiskProto.GetRiskStatusResponse>(StatusCode.Unauthenticated) };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        using var risk = Risk(host, token: null);

        (await new GrpcPauseController(risk, NullLogger<GrpcPauseController>.Instance).GetStatusAsync()).Succeeded.Should().BeFalse();
        behavior.Received.Should().ContainSingle().Which.Authorization.Should().BeEmpty();
    }
}
