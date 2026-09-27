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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationService.Features.Notifications;
using NotificationService.Infrastructure.ExternalServices;
using Xunit;
using MonitorApply = MarketMonitorWorker::MarketMonitorService.Features.MarketMonitor.ApplyWatchlistProposal;
using MonitorFeatures = MarketMonitorWorker::MarketMonitorService.Features.MarketMonitor;
using MonitorProto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;
using ReportFeatures = ReportWorker::ReportService.Features.Reports;
using ReportProto = AiStockTrading.Shared.Grpc.Report.V1;
using ReportRevise = ReportWorker::ReportService.Features.Reports.RevisePolicy;
using RiskAdopt = RiskManagementWorker::RiskManagementService.Features.RiskManagement.AdoptPositionDrift;
using RiskDomain = RiskManagementWorker::RiskManagementService.Domain;
using RiskFeatures = RiskManagementWorker::RiskManagementService.Features.RiskManagement;
using RiskProto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace NotificationService.Tests;

// T-10-1735, NFR, FR-14, FR-07, FR-09, FR-10, FR-11, FR-13, FR-19, FR-20, MSP:ADR-0029, ADR-0047 決定 1・2, IADR-0284 決定 5（段 5）,
// IADR-0450 決定 4, #753:
// Discord ボットの**書き込み 13 本**の gRPC 実装が REST 実装と同じ結果を返し、書き込み特有の規則（再試行しない・時間切れは「結果は不明」・
// 明確な失敗は「実行していない」・REST へ落とさない・利用者の文脈は本文）を守ることを固定する。
//
// 🔴 観測の仕方（パリティ）: **送り手の本物の値**（`StageTransitionResult`・`PositionDriftAdoptionResponse`・`PolicyRevisionResponse`・
// `WatchlistProposalApplyResponse` ほか）を 1 つ作り、REST 側は送り手の JSON 設定で直列化して `Http*` アダプタで読み、gRPC 側は**送り手の本物の写し**
// （`RiskWriteWireMapping` ほか）で proto にして実 Kestrel の h2c の偽の提供側（127.0.0.1）から返して `Grpc*` 実装で読み、結果を等価比較する。
// 失敗は REST の非 2xx（本文 `{"error":…}`）と、提供側の写しの表（IADR-0450 決定 2: 400 → INVALID_ARGUMENT・409 → ABORTED・422 → FAILED_PRECONDITION）の
// gRPC の失敗を並べて比べる。
public class GrpcBotWritesTests
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

    private static Grpc.Net.Client.GrpcChannel Channel(BotReadGrpcStubHost host) =>
        GrpcClientExtensions.CreateAiStockTradingChannel(host.Address, new FixedToken(Token));

    private static RiskManagementGrpcTransport Risk(BotReadGrpcStubHost host, TimeSpan? timeout = null, int attempts = 1) =>
        new(Channel(host), timeout ?? TimeSpan.FromSeconds(5), attempts, NullLogger<RiskManagementGrpcTransport>.Instance);

    private static ReportsGrpcTransport Reports(BotReadGrpcStubHost host, TimeSpan? timeout = null, int attempts = 1) =>
        new(Channel(host), timeout ?? TimeSpan.FromSeconds(5), attempts, NullLogger<ReportsGrpcTransport>.Instance, timeout);

    private static MarketMonitorGrpcTransport Monitor(BotReadGrpcStubHost host, TimeSpan? timeout = null, int attempts = 1) =>
        new(Channel(host), timeout ?? TimeSpan.FromSeconds(5), attempts, NullLogger<MarketMonitorGrpcTransport>.Instance);

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private static HttpClient RestReturning(object value, JsonSerializerOptions options, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new StubHandler(status, JsonSerializer.Serialize(value, value.GetType(), options))) { BaseAddress = new Uri("http://sender") };

    private static HttpClient RestError(HttpStatusCode status, string error) =>
        new(new StubHandler(status, JsonSerializer.Serialize(new { error }))) { BaseAddress = new Uri("http://sender") };

    private sealed record Controllers(
        GrpcKillSwitchController KillSwitch,
        GrpcPauseController Pause,
        GrpcGoodFaithViolationController GoodFaith,
        GrpcStageGateController StageGate,
        GrpcPositionDriftAdoptionController Adoption,
        GrpcReportReviewController Review,
        GrpcPolicyRevisionController Policy,
        GrpcMarketMonitorWatchlistController Watchlist);

    private static Controllers All(BotReadGrpcStubHost host, TimeSpan? timeout = null, int attempts = 1)
    {
        var risk = Risk(host, timeout, attempts);
        var reports = Reports(host, timeout, attempts);
        var monitor = Monitor(host, timeout, attempts);
        return new(
            new GrpcKillSwitchController(risk, NullLogger<GrpcKillSwitchController>.Instance),
            new GrpcPauseController(risk, NullLogger<GrpcPauseController>.Instance),
            new GrpcGoodFaithViolationController(risk, NullLogger<GrpcGoodFaithViolationController>.Instance),
            new GrpcStageGateController(risk, NullLogger<GrpcStageGateController>.Instance),
            new GrpcPositionDriftAdoptionController(risk, NullLogger<GrpcPositionDriftAdoptionController>.Instance),
            new GrpcReportReviewController(reports, NullLogger<GrpcReportReviewController>.Instance),
            new GrpcPolicyRevisionController(reports, NullLogger<GrpcPolicyRevisionController>.Instance),
            new GrpcMarketMonitorWatchlistController(monitor, NullLogger<GrpcMarketMonitorWatchlistController>.Instance));
    }

    // 13 本の書き込みを 1 回ずつ呼ぶ（結果は捨てる）。
    private static async Task CallAllWritesAsync(Controllers c)
    {
        await c.KillSwitch.EngageAsync("r");
        await c.KillSwitch.DisengageAsync("r");
        await c.Pause.PauseAsync("r");
        await c.Pause.ResumeAsync("r");
        await c.GoodFaith.ClearAsync("r");
        await c.StageGate.RequestTransitionAsync(1, "owner-a");
        await c.StageGate.EvaluateWithdrawalAsync();
        await c.Adoption.AdoptAsync("AAPL", Market.UnitedStates, "r", "owner-a");
        await c.Review.ConfirmAsync("daily-2026-09-01", 1, "owner-a");
        await c.Review.RequestChangesAsync("daily-2026-09-01", 1);
        await c.Policy.ReviseAsync(null, "i", "owner-a", null);
        await c.Policy.RecordWatchlistApplyAsync(Guid.NewGuid(), "applied", [], "m", "owner-a");
        await c.Watchlist.ApplyProposalAsync([], [new WatchlistChangeSuggestionView("add", "NVDA", "r")], "ref", "owner-a");
    }

    internal static readonly string[] WriteRpcs =
    [
        "EngageKillSwitch", "DisengageKillSwitch", "PauseTrading", "ResumeTrading", "ClearGoodFaithViolations", "RequestStageTransition",
        "EvaluateWithdrawal", "AdoptPositionDrift", "ConfirmReport", "RequestReportChanges", "RevisePolicy", "RecordWatchlistApplyResult",
        "ApplyWatchlistProposal",
    ];

    // ---- パリティ: 成功（送り手の本物の値を REST と gRPC で読ませて比べる） ----

    [Fact]
    public async Task T_10_1735_kill_switch_と一時停止は_REST_と同じ結果()
    {
        var kill = new RiskFeatures.KillSwitchState(true, "risk-bot", "急変", T0);
        var paused = new RiskFeatures.PauseState(false, "risk-bot", "再開", T0);
        var behavior = new BotReadStubBehavior
        {
            KillSwitch = BotReadStubBehavior.Returns(new RiskProto.KillSwitchChangeResponse { Engaged = kill.Engaged }),
            Pause = BotReadStubBehavior.Returns(new RiskProto.TradingPauseChangeResponse { Paused = paused.Paused }),
        };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        var c = All(host);

        (await c.KillSwitch.EngageAsync("急変")).Should().Be(
            await new HttpKillSwitchController(RestReturning(kill, RiskWire), NullLogger<HttpKillSwitchController>.Instance).EngageAsync("急変"));
        (await c.Pause.ResumeAsync("再開")).Should().Be(
            await new HttpPauseController(RestReturning(paused, RiskWire), NullLogger<HttpPauseController>.Instance).ResumeAsync("再開"));
        behavior.Requests.Select(r => r.Request).OfType<RiskProto.KillSwitchChangeRequest>().Single().Reason.Should().Be("急変");
        behavior.Received.Should().OnlyContain(r => r.Authorization == $"Bearer {Token}", "ボット自身の owner トークン（ADR-0047 決定 2）");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task T_10_1735_GFV_解除は_REST_と同じ結果で残件数を落とさない(int remaining)
    {
        var sent = new { clearedOrderIds = new[] { "o-1", "o-2" }, clearedAt = T0, remainingCount = remaining };
        var wire = new RiskProto.GoodFaithViolationClearanceResponse { ClearedAt = T0.ToString("O"), RemainingCount = remaining };
        wire.ClearedOrderIds.AddRange(sent.clearedOrderIds);
        var behavior = new BotReadStubBehavior { GoodFaith = BotReadStubBehavior.Returns(wire) };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);

        var grpc = await All(host).GoodFaith.ClearAsync("是正済み");
        var rest = await new HttpGoodFaithViolationController(RestReturning(sent, RiskWire), NullLogger<HttpGoodFaithViolationController>.Instance)
            .ClearAsync("是正済み");

        rest.Cleared.Should().BeTrue();
        grpc.Should().Be(rest);
        grpc.Message.Contains("停止は継続します").Should().Be(remaining > 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T_10_1735_段階遷移は_REST_と同じ結果と警告で承認者を本文で運ぶ(bool accepted)
    {
        var criteria = new RiskDomain.Stage1GateCriteria(60, 50, 120);   // 統計的根拠を下回る設定（警告が出る）
        var sent = accepted
            ? new RiskDomain.StageTransitionResult(
                true, new RiskDomain.StageTransition(1, TradingStage.Stage0Verification, TradingStage.Stage1Simulate, RiskDomain.StageTransitionKind.Promotion, "owner-a", T0, "昇格"),
                new RiskDomain.StageSettings(TradingStage.Stage1Simulate, BrokerProvider.MoomooSimulate, 1m), [], criteria)
            : new RiskDomain.StageTransitionResult(
                false, null, null, [RiskDomain.StageGateCriterion.PromotionMustBeSequential, RiskDomain.StageGateCriterion.BacktestNotPassed], criteria);
        var behavior = new BotReadStubBehavior { Transition = BotReadStubBehavior.Returns(RiskFeatures.RiskWriteWireMapping.ToProto(sent)) };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);

        var grpc = await All(host).StageGate.RequestTransitionAsync(accepted ? 1 : 2, "owner-a");
        var rest = await new HttpStageGateController(
            RestReturning(sent, RiskWire, accepted ? HttpStatusCode.OK : HttpStatusCode.UnprocessableEntity), NullLogger<HttpStageGateController>.Instance)
            .RequestTransitionAsync(accepted ? 1 : 2, "owner-a");

        (rest.Succeeded, rest.Accepted).Should().Be((true, accepted));
        grpc.Should().Be(rest);
        grpc.Stage1Warning.Should().NotBeNull("受理・拒否の両方で警告を載せる（#466）");
        var request = behavior.Requests.Select(r => r.Request).OfType<RiskProto.StageTransitionApprovalRequest>().Single();
        (request.OnBehalfOf, request.TargetStage).Should().Be(
            ("owner-a", accepted ? RiskProto.TradingStage.Stage1Simulate : RiskProto.TradingStage.Stage2MinimalLive),
            "承認者は本文の on_behalf_of（ADR-0047 決定 1）。段階は名前で写す（Stage 0 を未指定に化けさせない）");
    }

    [Fact]
    public async Task T_10_1735_撤退評価は_REST_と同じ結果()
    {
        var sent = new RiskDomain.WithdrawalAssessment(true, RiskDomain.WithdrawalReason.DrawdownBreachedMultiple, true, TradingStage.Stage0Verification);
        var behavior = new BotReadStubBehavior
        {
            Withdrawal = BotReadStubBehavior.Returns(new RiskProto.WithdrawalEvaluationResponse { Assessment = RiskFeatures.RiskWriteWireMapping.ToProto(sent) }),
        };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);

        var grpc = await All(host).StageGate.EvaluateWithdrawalAsync();
        var rest = await new HttpStageGateController(RestReturning(sent, RiskWire), NullLogger<HttpStageGateController>.Instance).EvaluateWithdrawalAsync();

        rest.Succeeded.Should().BeTrue();
        grpc.Should().Be(rest);
    }

    [Fact]
    public async Task T_10_1735_乖離の取り込みは_REST_と同じ結果で操作者と市場を本文で運ぶ()
    {
        var sent = new RiskAdopt.PositionDriftAdoptionResponse(
            Guid.NewGuid(), "7203", Market.Japan, 100, 0, 0, T0, false, null, null, T0.AddMinutes(1), "owner-a");
        var behavior = new BotReadStubBehavior { Adoption = BotReadStubBehavior.Returns(RiskFeatures.RiskWriteWireMapping.ToProto(sent)) };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);

        var grpc = await All(host).Adoption.AdoptAsync("7203", Market.Japan, "アプリで売却", "owner-a");
        var rest = await new HttpPositionDriftAdoptionController(RestReturning(sent, RiskWire), NullLogger<HttpPositionDriftAdoptionController>.Instance)
            .AdoptAsync("7203", Market.Japan, "アプリで売却", "owner-a");

        rest.Adopted.Should().BeTrue();
        grpc.Should().Be(rest);
        grpc.Message.Should().Contain("日本市場", "日本（C# の 0）は線上で未指定に化けない");
        var request = behavior.Requests.Select(r => r.Request).OfType<RiskProto.DriftAdoptionCommandRequest>().Single();
        (request.OnBehalfOf, request.Market, request.Symbol, request.Reason).Should().Be(("owner-a", RiskProto.Market.Japan, "7203", "アプリで売却"));
    }

    [Theory]
    [InlineData(true, 2)]    // この要求で確定した
    [InlineData(false, 2)]   // 冪等な再確定（この版で確定済み）
    [InlineData(false, 5)]   // 別の版で確定済み＝確定していない
    public async Task T_10_1735_報告書の確定は_REST_と同じ結果で確定者を本文で運ぶ(bool transitioned, int confirmedVersion)
    {
        var behavior = new BotReadStubBehavior
        {
            Confirm = BotReadStubBehavior.Returns(new ReportProto.ReportConfirmationResponse { Transitioned = transitioned, Version = confirmedVersion }),
        };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);

        var grpc = await All(host).Review.ConfirmAsync("daily-2026-09-01", 1, "owner-a");
        var rest = await new HttpReportReviewController(
            RestReturning(new { transitioned, version = confirmedVersion }, ReportWire), NullLogger<HttpReportReviewController>.Instance)
            .ConfirmAsync("daily-2026-09-01", 1, "owner-a");

        grpc.Should().Be(rest);
        grpc.Confirmed.Should().Be(transitioned || confirmedVersion == 2);
        behavior.Requests.Select(r => r.Request).OfType<ReportProto.ReportConfirmationRequest>().Single().OnBehalfOf.Should().Be("owner-a");
    }

    [Fact]
    public async Task T_10_1735_報告書の差し戻しは_REST_と同じ結果()
    {
        var sent = new ReportWorker::ReportService.Domain.ReportReview("daily-2026-09-01", ReportWorker::ReportService.Domain.ReviewState.ChangesRequested, 3);
        var behavior = new BotReadStubBehavior { Changes = BotReadStubBehavior.Returns(new ReportProto.ReportChangesResponse { Version = sent.Version }) };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);

        var grpc = await All(host).Review.RequestChangesAsync("daily-2026-09-01", 3);
        var rest = await new HttpReportReviewController(RestReturning(sent, ReportWire), NullLogger<HttpReportReviewController>.Instance)
            .RequestChangesAsync("daily-2026-09-01", 3);

        rest.Succeeded.Should().BeTrue();
        grpc.Should().Be(rest);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T_10_1735_方針の改訂は_REST_と同じ結果で監視銘柄の不明と空を区別して運ぶ(bool watchlistKnown)
    {
        var sent = new ReportRevise.PolicyRevisionResponse(
            "daily-2026-09-01", 3, true, true, "改訂案を提示しました", "押し目買いを優先する",
            [new ReportRevise.WatchlistChangeView("add", "NVDA", "出来高"), new ReportRevise.WatchlistChangeView("remove", "7203", "")], null);
        var behavior = new BotReadStubBehavior { Revision = BotReadStubBehavior.Returns(ReportFeatures.ReportWriteWireMapping.ToProto(sent)) };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        IReadOnlyList<WatchlistSnapshotItemView>? current = watchlistKnown ? [] : null;

        var grpc = await All(host).Policy.ReviseAsync("daily-2026-09-01", "慎重に", "owner-a", current);
        var rest = await new HttpPolicyRevisionController(RestReturning(sent, ReportWire), NullLogger<HttpPolicyRevisionController>.Instance)
            .ReviseAsync("daily-2026-09-01", "慎重に", "owner-a", current);

        rest.Succeeded.Should().BeTrue();
        grpc.Should().BeEquivalentTo(rest);
        var request = behavior.Requests.Select(r => r.Request).OfType<ReportProto.PolicyRevisionProposalRequest>().Single();
        (request.OnBehalfOf, request.PeriodKey, request.Instruction).Should().Be(("owner-a", "daily-2026-09-01", "慎重に"));
        (request.CurrentWatchlist is not null).Should().Be(watchlistKnown, "照会できなかった（null）を空の一覧と取り違えない");
    }

    [Fact]
    public async Task T_10_1735_適用の内訳の記録と入れ替え案の適用は_REST_と同じ結果()
    {
        var applied = new MonitorApply.WatchlistProposalApplyResponse(
            [new MonitorApply.WatchlistProposalItemResult("add", "NVDA", true, null), new MonitorApply.WatchlistProposalItemResult("add", "MSFT", false, "既に監視対象")],
            "owner-a",
            new MonitorApply.FinnhubDailyVolumeEstimateView(1170, null, false));
        var behavior = new BotReadStubBehavior
        {
            Apply = BotReadStubBehavior.Returns(MonitorFeatures.WatchlistOwnerWriteGrpcService.ToProto(applied)),
            ApplyRecord = BotReadStubBehavior.Returns(new ReportProto.WatchlistApplyRecordResponse { AttemptId = Guid.Empty.ToString() }),
        };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        var c = All(host);
        WatchlistSnapshotItemView[] expected = [new("7203", "Japan"), new("AAPL", "UnitedStates")];
        WatchlistChangeSuggestionView[] changes = [new("add", "NVDA", "出来高"), new("add", "MSFT", "重複")];

        var grpc = await c.Watchlist.ApplyProposalAsync(expected, changes, "daily-2026-09-01-v3", "owner-a");
        var rest = await new HttpMarketMonitorWatchlistController(RestReturning(applied, MonitorWire), NullLogger<HttpMarketMonitorWatchlistController>.Instance)
            .ApplyProposalAsync(expected, changes, "daily-2026-09-01-v3", "owner-a");
        (await c.Policy.RecordWatchlistApplyAsync(Guid.NewGuid(), "applied", grpc.Items, "m", "owner-a")).Should().BeTrue();

        rest.Status.Should().Be(WatchlistApplyStatus.Applied);
        grpc.Should().BeEquivalentTo(rest);
        var request = behavior.Requests.Select(r => r.Request).OfType<MonitorProto.WatchlistProposalApplicationRequest>().Single();
        request.ExpectedWatchlist.Items.Select(i => i.Market).Should().Equal(MonitorProto.Market.Japan, MonitorProto.Market.UnitedStates);
        (request.OnBehalfOf, request.ProposalRef).Should().Be(("owner-a", "daily-2026-09-01-v3"));
        behavior.Requests.Select(r => r.Request).OfType<ReportProto.WatchlistApplyRecordRequest>().Single().OnBehalfOf.Should().Be("owner-a");
    }

    // ---- パリティ: 提供側が明確に拒否した（REST の非 2xx ＝ gRPC の明確な失敗）は同じ結果＝「実行していない」 ----

    [Fact]
    public async Task T_10_1735_受理不能と入力の誤りは_REST_と同じ結果と文言()
    {
        const string Why = "提供側の説明";
        var behavior = new BotReadStubBehavior
        {
            GoodFaith = BotReadStubBehavior.Fails<RiskProto.GoodFaithViolationClearanceResponse>(StatusCode.FailedPrecondition, Why),
            Transition = BotReadStubBehavior.Fails<RiskProto.StageTransitionApprovalResponse>(StatusCode.InvalidArgument, Why),
            Adoption = BotReadStubBehavior.Fails<RiskProto.DriftAdoptionCommandResponse>(StatusCode.FailedPrecondition, Why),
            Confirm = BotReadStubBehavior.Fails<ReportProto.ReportConfirmationResponse>(StatusCode.Aborted, Why),
            Changes = BotReadStubBehavior.Fails<ReportProto.ReportChangesResponse>(StatusCode.NotFound, Why),
            Revision = BotReadStubBehavior.Fails<ReportProto.PolicyRevisionProposalResponse>(StatusCode.ResourceExhausted, Why),
            ApplyRecord = BotReadStubBehavior.Fails<ReportProto.WatchlistApplyRecordResponse>(StatusCode.Aborted, Why),
            Apply = BotReadStubBehavior.Fails<MonitorProto.WatchlistProposalApplicationResponse>(StatusCode.Aborted, Why),
        };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        var c = All(host);
        WatchlistSnapshotItemView[] expected = [new("AAPL", "UnitedStates")];
        WatchlistChangeSuggestionView[] changes = [new("add", "NVDA", "r")];

        (await c.GoodFaith.ClearAsync("r")).Should().Be(await new HttpGoodFaithViolationController(
            RestError(HttpStatusCode.UnprocessableEntity, Why), NullLogger<HttpGoodFaithViolationController>.Instance).ClearAsync("r"));
        (await c.StageGate.RequestTransitionAsync(1, "owner-a")).Should().Be(await new HttpStageGateController(
            RestError(HttpStatusCode.BadRequest, Why), NullLogger<HttpStageGateController>.Instance).RequestTransitionAsync(1, "owner-a"));
        var adopt = await c.Adoption.AdoptAsync("AAPL", Market.UnitedStates, "r", "owner-a");
        adopt.Should().Be(await new HttpPositionDriftAdoptionController(
            RestError(HttpStatusCode.UnprocessableEntity, Why), NullLogger<HttpPositionDriftAdoptionController>.Instance)
            .AdoptAsync("AAPL", Market.UnitedStates, "r", "owner-a"));
        (await c.Review.ConfirmAsync("k", 1, "owner-a")).Should().Be(await new HttpReportReviewController(
            RestError(HttpStatusCode.Conflict, Why), NullLogger<HttpReportReviewController>.Instance).ConfirmAsync("k", 1, "owner-a"));
        (await c.Review.RequestChangesAsync("k", 1)).Should().Be(await new HttpReportReviewController(
            RestError(HttpStatusCode.NotFound, Why), NullLogger<HttpReportReviewController>.Instance).RequestChangesAsync("k", 1));
        var revise = await c.Policy.ReviseAsync(null, "i", "owner-a", null);
        revise.Should().BeEquivalentTo(await new HttpPolicyRevisionController(
            RestError(HttpStatusCode.TooManyRequests, Why), NullLogger<HttpPolicyRevisionController>.Instance).ReviseAsync(null, "i", "owner-a", null));
        (await c.Policy.RecordWatchlistApplyAsync(Guid.NewGuid(), "applied", [], "m", "owner-a")).Should().BeFalse();
        var apply = await c.Watchlist.ApplyProposalAsync(expected, changes, "ref", "owner-a");
        apply.Should().BeEquivalentTo(await new HttpMarketMonitorWatchlistController(
            RestError(HttpStatusCode.Conflict, Why), NullLogger<HttpMarketMonitorWatchlistController>.Instance).ApplyProposalAsync(expected, changes, "ref", "owner-a"));

        (adopt.Succeeded, adopt.Adopted).Should().Be((true, false), "受理不能は「明確に応答した」（台帳は動いていない）");
        (revise.Succeeded, revise.Indeterminate).Should().Be((false, false), "明確な拒否は「案なし」（不明ではない）");
        apply.Status.Should().Be(WatchlistApplyStatus.Stale);
    }

    // ---- 書き込みの規則 ----

    // 🔴 時間切れは「結果は不明」（書き込みでは正しい。状態を騙らない）。per-call 1 秒・上限 20 秒（10 倍以上）。
    [Fact]
    public async Task T_10_1735_応答が来なければ試行ごとの上限で打ち切り結果は不明と返す()
    {
        var behavior = new BotReadStubBehavior
        {
            KillSwitch = BotReadStubBehavior.Hangs<RiskProto.KillSwitchChangeResponse>(),
            Pause = BotReadStubBehavior.Hangs<RiskProto.TradingPauseChangeResponse>(),
            GoodFaith = BotReadStubBehavior.Hangs<RiskProto.GoodFaithViolationClearanceResponse>(),
            Transition = BotReadStubBehavior.Hangs<RiskProto.StageTransitionApprovalResponse>(),
            Withdrawal = BotReadStubBehavior.Hangs<RiskProto.WithdrawalEvaluationResponse>(),
            Adoption = BotReadStubBehavior.Hangs<RiskProto.DriftAdoptionCommandResponse>(),
            Confirm = BotReadStubBehavior.Hangs<ReportProto.ReportConfirmationResponse>(),
            Changes = BotReadStubBehavior.Hangs<ReportProto.ReportChangesResponse>(),
            Revision = BotReadStubBehavior.Hangs<ReportProto.PolicyRevisionProposalResponse>(),
            Apply = BotReadStubBehavior.Hangs<MonitorProto.WatchlistProposalApplicationResponse>(),
        };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        var c = All(host, TimeSpan.FromSeconds(1));

        var watch = Stopwatch.StartNew();
        var kill = await c.KillSwitch.EngageAsync("r");
        var pause = await c.Pause.PauseAsync("r");
        var gfv = await c.GoodFaith.ClearAsync("r");
        var transition = await c.StageGate.RequestTransitionAsync(1, "owner-a");
        var withdrawal = await c.StageGate.EvaluateWithdrawalAsync();
        var adopt = await c.Adoption.AdoptAsync("AAPL", Market.UnitedStates, "r", "owner-a");
        var confirm = await c.Review.ConfirmAsync("k", 1, "owner-a");
        var changes = await c.Review.RequestChangesAsync("k", 1);
        var revise = await c.Policy.ReviseAsync(null, "i", "owner-a", null);
        var apply = await c.Watchlist.ApplyProposalAsync([], [new("add", "NVDA", "r")], "ref", "owner-a");
        watch.Stop();

        kill.Message.Should().Be(HttpKillSwitchController.TimedOutMessage("起動"));
        pause.Message.Should().Be(HttpPauseController.TimedOutMessage("一時停止"));
        gfv.Message.Should().Be(HttpGoodFaithViolationController.TimedOutMessage);
        transition.Message.Should().Be(HttpStageGateController.TransitionTimedOutMessage);
        withdrawal.Message.Should().Be(HttpStageGateController.WithdrawalTimedOutMessage);
        adopt.Message.Should().Be(HttpPositionDriftAdoptionController.TimedOutMessage);
        confirm.Should().Be(new ReportConfirmResult(false, false, "報告書の確定がタイムアウトしました（結果は不明です）"));
        changes.Should().Be(new ReportReviewResult(false, 0, "報告書の差し戻しがタイムアウトしました（結果は不明です）"));
        (revise.Succeeded, revise.Indeterminate, revise.Message).Should().Be((false, true, HttpPolicyRevisionController.RevisionUnknownMessage));
        (apply.Status, apply.Message).Should().Be((WatchlistApplyStatus.Indeterminate, HttpMarketMonitorWatchlistController.ApplyUnknownMessage));
        foreach (var message in new[] { kill.Message, pause.Message, gfv.Message, transition.Message, withdrawal.Message, adopt.Message })
            message.Should().Contain("不明", "書き込みの時間切れは「結果は不明」");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
    }

    // 🔴 書き込みは構成（GrpcMaxAttempts）に関わらず再試行しない。2 回目は受理不能・二重実行になり得る（IADR-0450 決定 4）。
    [Fact]
    public async Task T_10_1735_書き込みは試行回数を宣言しても再試行しない()
    {
        var behavior = new BotReadStubBehavior
        {
            KillSwitch = BotReadStubBehavior.Fails<RiskProto.KillSwitchChangeResponse>(StatusCode.Unavailable),
            Pause = BotReadStubBehavior.Fails<RiskProto.TradingPauseChangeResponse>(StatusCode.Unavailable),
            GoodFaith = BotReadStubBehavior.Fails<RiskProto.GoodFaithViolationClearanceResponse>(StatusCode.Unavailable),
            Transition = BotReadStubBehavior.Fails<RiskProto.StageTransitionApprovalResponse>(StatusCode.Unavailable),
            Withdrawal = BotReadStubBehavior.Fails<RiskProto.WithdrawalEvaluationResponse>(StatusCode.Unavailable),
            Adoption = BotReadStubBehavior.Fails<RiskProto.DriftAdoptionCommandResponse>(StatusCode.Unavailable),
            Confirm = BotReadStubBehavior.Fails<ReportProto.ReportConfirmationResponse>(StatusCode.Unavailable),
            Changes = BotReadStubBehavior.Fails<ReportProto.ReportChangesResponse>(StatusCode.Unavailable),
            Revision = BotReadStubBehavior.Fails<ReportProto.PolicyRevisionProposalResponse>(StatusCode.Unavailable),
            ApplyRecord = BotReadStubBehavior.Fails<ReportProto.WatchlistApplyRecordResponse>(StatusCode.Unavailable),
            Apply = BotReadStubBehavior.Fails<MonitorProto.WatchlistProposalApplicationResponse>(StatusCode.Unavailable),
        };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);

        await CallAllWritesAsync(All(host, attempts: 3));

        foreach (var rpc in WriteRpcs)
            behavior.Calls(rpc).Should().Be(1, $"{rpc} は再試行しない");
    }

    // 提供側が古い（UNIMPLEMENTED）・未認証は「実行していない」＝明確な失敗（「不明」にしない）。届いたか分からない失敗（UNAVAILABLE）は、
    // REST が例外を「不明」と言う操作（方針の改訂・入れ替えの適用）でだけ「不明」。
    [Theory]
    [InlineData(StatusCode.Unimplemented, false)]
    [InlineData(StatusCode.Unauthenticated, false)]
    [InlineData(StatusCode.Internal, false)]
    [InlineData(StatusCode.Unavailable, true)]
    public async Task T_10_1735_明確な失敗は実行していない_届いたか分からない失敗は不明(StatusCode status, bool unknown)
    {
        var behavior = new BotReadStubBehavior
        {
            KillSwitch = BotReadStubBehavior.Fails<RiskProto.KillSwitchChangeResponse>(status),
            Revision = BotReadStubBehavior.Fails<ReportProto.PolicyRevisionProposalResponse>(status),
            Apply = BotReadStubBehavior.Fails<MonitorProto.WatchlistProposalApplicationResponse>(status),
        };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        var c = All(host);

        var kill = await c.KillSwitch.EngageAsync("r");
        var revise = await c.Policy.ReviseAsync(null, "i", "owner-a", null);
        var apply = await c.Watchlist.ApplyProposalAsync([], [new("add", "NVDA", "r")], "ref", "owner-a");

        (kill.Succeeded, kill.Engaged).Should().Be((false, false));
        kill.Message.Should().Contain(status.ToString());
        kill.Message.Contains(NotificationGrpcCalls.OwnerHint).Should().Be(status == StatusCode.Unauthenticated);
        revise.Indeterminate.Should().Be(unknown);
        apply.Status.Should().Be(unknown ? WatchlistApplyStatus.Indeterminate : WatchlistApplyStatus.Rejected);
        // 認証・未実装・不達の詳細は gRPC の基盤が作る技術的な文なので見せない（INTERNAL は提供側が 502 の説明を載せる状態なので見せる）。
        revise.Message.Contains("stub failure").Should().Be(status == StatusCode.Internal);
    }

    // 🔴 原則 A: 必須の項目の欠落は既定値（false・0）で読まない。書き込みでは「適用されたか分からない」側へ倒す。
    [Fact]
    public async Task T_10_1735_必須の項目が欠けた応答は成功に見せない()
    {
        var behavior = new BotReadStubBehavior
        {
            KillSwitch = BotReadStubBehavior.Returns(new RiskProto.KillSwitchChangeResponse()),
            Pause = BotReadStubBehavior.Returns(new RiskProto.TradingPauseChangeResponse()),
            GoodFaith = BotReadStubBehavior.Returns(new RiskProto.GoodFaithViolationClearanceResponse()),
            Transition = BotReadStubBehavior.Returns(new RiskProto.StageTransitionApprovalResponse()),
            Withdrawal = BotReadStubBehavior.Returns(new RiskProto.WithdrawalEvaluationResponse()),
            Adoption = BotReadStubBehavior.Returns(new RiskProto.DriftAdoptionCommandResponse { AdoptionId = Guid.NewGuid().ToString() }),
            Confirm = BotReadStubBehavior.Returns(new ReportProto.ReportConfirmationResponse { Transitioned = true }),
            Changes = BotReadStubBehavior.Returns(new ReportProto.ReportChangesResponse()),
            Revision = BotReadStubBehavior.Returns(new ReportProto.PolicyRevisionProposalResponse { PeriodKey = "k", Version = 2, PolicySummary = "s" }),
            Apply = BotReadStubBehavior.Returns(new MonitorProto.WatchlistProposalApplicationResponse
            {
                Items = { new MonitorProto.WatchlistApplicationItem { Action = "add", Symbol = "NVDA" } },   // 適用の真偽が欠落
            }),
        };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        var c = All(host);

        (await c.KillSwitch.EngageAsync("r")).Should().Be(new KillSwitchResult(false, false, HttpKillSwitchController.UnparsableMessage("起動")));
        (await c.Pause.PauseAsync("r")).Should().Be(new PauseResult(false, false, HttpPauseController.UnparsableMessage("一時停止")));
        (await c.GoodFaith.ClearAsync("r")).Should().Be(new GoodFaithViolationClearResult(false, false, HttpGoodFaithViolationController.UnparsableMessage));
        (await c.StageGate.RequestTransitionAsync(1, "owner-a")).Should().Be(
            new StageTransitionCommandResult(false, false, HttpStageGateController.TransitionUnparsableMessage));
        (await c.StageGate.EvaluateWithdrawalAsync()).Should().Be(new StageGateStatusResult(false, HttpStageGateController.WithdrawalUnparsableMessage));
        (await c.Adoption.AdoptAsync("AAPL", Market.UnitedStates, "r", "owner-a")).Should().Be(
            new PositionDriftAdoptionResult(false, false, HttpPositionDriftAdoptionController.UnparsableMessage));
        (await c.Review.ConfirmAsync("k", 1, "owner-a")).Should().Be(
            new ReportConfirmResult(false, false, GrpcReportReviewController.ConfirmUnparsableMessage), "確定したと騙らない（版の欠落）");
        (await c.Review.RequestChangesAsync("k", 1)).Should().Be(new ReportReviewResult(false, 0, HttpReportReviewController.ChangesUnparsableMessage));
        var revise = await c.Policy.ReviseAsync(null, "i", "owner-a", null);
        (revise.Succeeded, revise.Indeterminate).Should().Be((false, true), "保存されたかもしれない（200 は保存の後に返る）");
        (await c.Watchlist.ApplyProposalAsync([], [new("add", "NVDA", "r")], "ref", "owner-a")).Status.Should().Be(WatchlistApplyStatus.Indeterminate);
    }

    // ---- PR #1069 の監査 3: 監視銘柄の解釈の失敗は Warning を残す（他の Grpc* と揃える） ----

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        internal List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task T_10_1735_監視銘柄の応答を解釈できなければ_Warning_を残す()
    {
        var behavior = new BotReadStubBehavior
        {
            Watchlist = BotReadStubBehavior.Returns(new MonitorProto.GetWatchlistResponse { Items = { new MonitorProto.WatchlistItem { Symbol = "7203" } } }),
        };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        var logger = new RecordingLogger<GrpcMarketMonitorWatchlistController>();

        var result = await new GrpcMarketMonitorWatchlistController(Monitor(host), logger).GetWatchlistAsync();

        result.Succeeded.Should().BeFalse();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains("監視銘柄の応答を解釈できませんでした"));
    }
}
