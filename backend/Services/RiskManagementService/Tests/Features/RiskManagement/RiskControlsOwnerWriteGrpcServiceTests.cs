using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.AdoptPositionDrift;
using Wolverine.Tracking;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace RiskManagementService.Tests;

// T-10-1732（提供側）, NFR, NFR-06, FR-10, FR-11, FR-14, FR-19, FR-20, MSP:ADR-0029, ADR-0047 決定 1〜3, IADR-0284 決定 5（段 5）,
// IADR-0450 決定 2・3, #753:
// Discord ボットが呼ぶリスク管理の**所有者限定の書き込みの gRPC 面**（`RiskControlsOwnerWrite` の 8 rpc）。REST の端点と**同じ処理関数**を通ること
// （同じ状態の変化・同じ操作者の記録〔on_behalf_of は信頼クライアントのトークンに限る〕・同じ拒否の分類と文言）と、門が `GrpcOwnerOnly`
// （ボット可・s2s 不可・人の利用者不可）であることを、本物の Program.cs（RiskWorkerWebApplicationFactory）で固定する。
// 🔴 呼べること自体が `MapGrpcService` の登録の証拠である（外すと UNIMPLEMENTED）。
public class RiskControlsOwnerWriteGrpcServiceTests
{
    private const string OwnerRole = "trading-owner";
    private const string ServiceRole = "trading-service";
    private const string Bot = "ai-stock-trading-owner";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static WebApplicationFactory<Program> Trusted(RiskWorkerWebApplicationFactory baseFactory) =>
        baseFactory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DelegatedActorOptions.TrustedClientIdsKey] = Bot,
            })));

    // name: null ＝ボットの実トークンの形（名前クレーム無し）。
    private sealed class Headers(HttpMessageHandler inner, string? roles, string? azp, string? name) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (roles is not null)
            {
                request.Headers.TryAddWithoutValidation(TestAuthHandler.RolesHeader, roles);
                request.Headers.TryAddWithoutValidation(TestAuthHandler.NameHeader, name ?? TestAuthHandler.NoName);
            }

            if (azp is not null)
                request.Headers.TryAddWithoutValidation(TestAuthHandler.AzpHeader, azp);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static Proto.RiskControlsOwnerWrite.RiskControlsOwnerWriteClient Grpc(
        WebApplicationFactory<Program> factory, string? roles = OwnerRole, string? azp = Bot, string? name = "risk-bot") =>
        new(GrpcChannel.ForAddress(factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = new Headers(factory.Server.CreateHandler(), roles, azp, name) }));

    private static HttpClient Rest(WebApplicationFactory<Program> factory, string? name = "risk-bot")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);
        client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, name ?? TestAuthHandler.NoName);
        client.DefaultRequestHeaders.Add(TestAuthHandler.AzpHeader, Bot);
        return client;
    }

    private static async Task<(int Status, string? Error)> PostAsync(HttpClient rest, string path, object? body)
    {
        using var response = body is null ? await rest.PostAsync(path, null) : await rest.PostAsJsonAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();
        string? error = null;
        if (!response.IsSuccessStatusCode && text.Length > 0)
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out var e))
                error = e.GetString();
        }

        return ((int)response.StatusCode, error);
    }

    private static async Task<RpcException> FailsAsync(Func<Task> act) => (await act.Should().ThrowAsync<RpcException>()).Which;

    // ---- kill switch・一時停止/再開: REST と同じ状態の変化（操作者はトークンの名前・理由は本文） ----

    [Fact]
    public async Task T_10_1732_kill_switch_と一時停止は_REST_と同じ状態を作る()
    {
        await using var restHost = new RiskWorkerWebApplicationFactory();
        await using var grpcHost = new RiskWorkerWebApplicationFactory();
        var rest = Rest(restHost);
        var grpc = Grpc(grpcHost);

        await PostAsync(rest, "/risk-controls/kill-switch/engage", new { reason = "急変" });
        var engaged = await grpc.EngageKillSwitchAsync(new Proto.KillSwitchChangeRequest { Reason = "急変" });
        await PostAsync(rest, "/risk-controls/pause", new { reason = "様子見" });
        var paused = await grpc.PauseTradingAsync(new Proto.TradingPauseChangeRequest { Reason = "様子見" });

        (engaged.HasEngaged, engaged.Engaged, paused.HasPaused, paused.Paused).Should().Be((true, true, true, true));
        var restKill = await Rest(restHost).GetFromJsonAsync<KillSwitchState>("/risk-controls/kill-switch", Web);
        var grpcKill = await Rest(grpcHost).GetFromJsonAsync<KillSwitchState>("/risk-controls/kill-switch", Web);
        (grpcKill!.Engaged, grpcKill.Actor, grpcKill.Reason).Should().Be((restKill!.Engaged, restKill.Actor, restKill.Reason));
        grpcKill.Actor.Should().Be("risk-bot", "操作者は REST と同じくトークンの名前");
        var restPause = await Rest(restHost).GetFromJsonAsync<PauseState>("/risk-controls/pause", Web);
        var grpcPause = await Rest(grpcHost).GetFromJsonAsync<PauseState>("/risk-controls/pause", Web);
        (grpcPause!.Paused, grpcPause.Actor, grpcPause.Reason).Should().Be((restPause!.Paused, restPause.Actor, restPause.Reason));

        var disengaged = await grpc.DisengageKillSwitchAsync(new Proto.KillSwitchChangeRequest { Reason = "収束" });
        var resumed = await grpc.ResumeTradingAsync(new Proto.TradingPauseChangeRequest { Reason = "再開" });
        (disengaged.HasEngaged, disengaged.Engaged, resumed.HasPaused, resumed.Paused).Should().Be((true, false, true, false),
            "false も値として運ぶ（欠落と区別する）");
    }

    // 群のフィルタの例外の写し（ArgumentException → 400）を REST と共有する: 理由の欠如は INVALID_ARGUMENT で同じ文言。
    [Fact]
    public async Task T_10_1732_理由の欠如は_REST_の_400_と同じ文言の_INVALID_ARGUMENT()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();

        var (status, error) = await PostAsync(Rest(factory), "/risk-controls/kill-switch/engage", new { reason = "" });
        var ex = await FailsAsync(async () => await Grpc(factory).EngageKillSwitchAsync(new Proto.KillSwitchChangeRequest { Reason = "" }));

        status.Should().Be(400);
        error.Should().NotBeNullOrEmpty();
        (ex.StatusCode, ex.Status.Detail).Should().Be((StatusCode.InvalidArgument, error!));
        (await Rest(factory).GetFromJsonAsync<KillSwitchState>("/risk-controls/kill-switch", Web))!.Engaged.Should().BeFalse();
    }

    // ---- GFV 解除: 解除対象なし（422）→ FAILED_PRECONDITION・理由の欠如（400）→ INVALID_ARGUMENT。文言は REST と同じ ----

    [Theory]
    [InlineData("原因を是正した", 422, StatusCode.FailedPrecondition)]
    [InlineData("", 400, StatusCode.InvalidArgument)]
    public async Task T_10_1732_GFV_解除の拒否は_REST_と同じ分類と文言(string reason, int restStatus, StatusCode grpcStatus)
    {
        await using var factory = new RiskWorkerWebApplicationFactory();

        var (status, error) = await PostAsync(Rest(factory), "/risk-controls/good-faith-violations/clear", new { reason });
        var ex = await FailsAsync(async () =>
            await Grpc(factory).ClearGoodFaithViolationsAsync(new Proto.GoodFaithViolationClearanceRequest { Reason = reason }));

        status.Should().Be(restStatus);
        (ex.StatusCode, ex.Status.Detail).Should().Be((grpcStatus, error!));
    }

    // ---- 段階遷移: 承認者は on_behalf_of（信頼クライアントに限る）。受理不能は accepted=false、入力の誤りは INVALID_ARGUMENT ----

    [Fact]
    public async Task T_10_1732_段階遷移は_REST_と同じ承認者で受理し受理不能は本文で返す()
    {
        await using var restBase = new RiskWorkerWebApplicationFactory();
        await using var grpcBase = new RiskWorkerWebApplicationFactory();
        var restHost = Trusted(restBase);
        var grpcHost = Trusted(grpcBase);
        foreach (var host in new[] { restHost, grpcHost })
        {
            using var scope = host.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<IStagePerformanceStore>().Save(new StagePerformance { BacktestPassed = true });
        }

        // 飛び級（受理不能＝REST の 422）。
        using var skipped = await Rest(restHost, name: null).PostAsJsonAsync(
            "/risk-controls/stage-gate/transition", new { targetStage = 2, onBehalfOf = "owner-a" });
        var restSkipped = await skipped.Content.ReadFromJsonAsync<StageTransitionResult>(Web);
        var grpcSkipped = await Grpc(grpcHost, name: null).RequestStageTransitionAsync(new Proto.StageTransitionApprovalRequest
        {
            TargetStage = Proto.TradingStage.Stage2MinimalLive,
            OnBehalfOf = "owner-a",
        });
        skipped.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        grpcSkipped.Should().Be(RiskWriteWireMapping.ToProto(restSkipped!));
        grpcSkipped.RejectionReasons.Should().NotBeEmpty("空どうしの一致は何も証明しない");
        grpcSkipped.HasAccepted.Should().BeTrue("false も値として運ぶ");

        // 受理。承認者は代理される利用者（名前クレームの無いボットのトークン＋信頼クライアント）。
        using var promoted = await Rest(restHost, name: null).PostAsJsonAsync(
            "/risk-controls/stage-gate/transition", new { targetStage = 1, onBehalfOf = "owner-a" });
        var restPromoted = await promoted.Content.ReadFromJsonAsync<StageTransitionResult>(Web);
        var grpcPromoted = await Grpc(grpcHost, name: null).RequestStageTransitionAsync(new Proto.StageTransitionApprovalRequest
        {
            TargetStage = Proto.TradingStage.Stage1Simulate,
            OnBehalfOf = "owner-a",
        });
        promoted.StatusCode.Should().Be(HttpStatusCode.OK);
        grpcPromoted.Should().Be(RiskWriteWireMapping.ToProto(restPromoted!));
        grpcPromoted.ToStage.Should().Be(Proto.TradingStage.Stage1Simulate);

        using var scope2 = grpcHost.Services.CreateScope();
        scope2.ServiceProvider.GetRequiredService<StageGateService>().GetHistory()
            .Should().ContainSingle().Which.ApprovedBy.Should().Be("owner-a", "承認者は on_behalf_of（REST と同じ DelegatedActorResolver）");
    }

    [Fact]
    public async Task T_10_1732_段階遷移の入力の誤りは_REST_の_400_と同じ文言の_INVALID_ARGUMENT()
    {
        await using var baseFactory = new RiskWorkerWebApplicationFactory();
        var factory = Trusted(baseFactory);

        var cases = new (object RestBody, Proto.StageTransitionApprovalRequest GrpcBody)[]
        {
            // 未指定の段階（REST の targetStage の省略）。
            (new { onBehalfOf = "owner-a" }, new Proto.StageTransitionApprovalRequest { OnBehalfOf = "owner-a" }),
            // 代理される利用者の値域外。
            (new { targetStage = 1, onBehalfOf = "bad name\n" },
                new Proto.StageTransitionApprovalRequest { TargetStage = Proto.TradingStage.Stage1Simulate, OnBehalfOf = "bad name\n" }),
        };
        foreach (var (restBody, grpcBody) in cases)
        {
            var (status, error) = await PostAsync(Rest(factory, name: null), "/risk-controls/stage-gate/transition", restBody);
            var ex = await FailsAsync(async () => await Grpc(factory, name: null).RequestStageTransitionAsync(grpcBody));

            status.Should().Be(400);
            (ex.StatusCode, ex.Status.Detail).Should().Be((StatusCode.InvalidArgument, error!));
        }

        // 🔴 承認者をまったく特定できない（名前も信頼される代理も無い）遷移は行わない（REST と同じ 400）。
        var anonymous = await FailsAsync(async () => await Grpc(baseFactory, azp: null, name: null).RequestStageTransitionAsync(
            new Proto.StageTransitionApprovalRequest { TargetStage = Proto.TradingStage.Stage1Simulate }));
        anonymous.StatusCode.Should().Be(StatusCode.PermissionDenied, "そもそも azp の無いトークンは gRPC の門を通らない");
    }

    // ---- 撤退評価 ----

    [Fact]
    public async Task T_10_1732_撤退評価は_REST_と同じ評価を返す()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();

        using var response = await Rest(factory).PostAsync("/risk-controls/stage-gate/withdrawal/evaluate", null);
        var rest = await response.Content.ReadFromJsonAsync<WithdrawalAssessment>(Web);
        var grpc = await Grpc(factory).EvaluateWithdrawalAsync(new Proto.WithdrawalEvaluationRequest());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        grpc.Assessment.Should().Be(RiskWriteWireMapping.ToProto(rest!));
        (grpc.Assessment.HasTriggered, grpc.Assessment.HasHaltNewEntries).Should().Be((true, true), "false も値として運ぶ");
    }

    // ---- 乖離の取り込み ----

    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-2);

    // 台帳へ建玉 100 株を積み、全株が消えた観測を 2 回届けて乖離を報告済みにする（PositionDriftAdoptionDelegatedActorTests と同じ種）。
    private static void SeedReportedDrift(WebApplicationFactory<Program> factory)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IPortfolioLedgerStore>();
            var decisionId = Guid.NewGuid();
            var at = DateTimeOffset.UtcNow.AddDays(-1);
            ledger.AppendApproval(
                decisionId,
                new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
                    100, 1m, PositionEffect.Open, StopLossPrice: 0.95m),
                at);
            ledger.AppendFill(decisionId, $"open-{decisionId:N}", 100, 1m, at);
        }

        for (var i = 0; i < 2; i++)
        {
            using var scope = factory.Services.CreateScope();
            var sp = scope.ServiceProvider;
            BrokerPositionSnapshot[] none = [];
            sp.GetRequiredService<IBrokerPositionObservationStore>().Record(none, ObservedAt.AddSeconds(i - 1));
            var drifts = PositionDriftDetector.Detect(
                PortfolioProjection.ProjectOpenPositions(sp.GetRequiredService<IPortfolioLedgerStore>().GetFills()), none);
            sp.GetRequiredService<PositionDriftTracker>().ShouldReport(drifts);
        }
    }

    [Fact]
    public async Task T_10_1732_乖離の取り込みは_REST_と同じ記録を作り操作者は代理される利用者()
    {
        await using var restBase = new RiskWorkerWebApplicationFactory();
        await using var grpcBase = new RiskWorkerWebApplicationFactory();
        var restHost = Trusted(restBase);
        var grpcHost = Trusted(grpcBase);
        SeedReportedDrift(restHost);
        SeedReportedDrift(grpcHost);

        using var response = await Rest(restHost, name: null).PostAsJsonAsync("/risk-controls/position-drift/adopt",
            new { symbol = "AAPL", market = (int)Market.UnitedStates, reason = "アプリで売却", onBehalfOf = "owner-a" });
        var rest = await response.Content.ReadFromJsonAsync<PositionDriftAdoptionResponse>(Web);
        var grpc = await Grpc(grpcHost, name: null).AdoptPositionDriftAsync(new Proto.DriftAdoptionCommandRequest
        {
            Symbol = "AAPL",
            Market = Proto.Market.UnitedStates,
            Reason = "アプリで売却",
            OnBehalfOf = "owner-a",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var expected = RiskWriteWireMapping.ToProto(rest!);
        expected.AdoptionId = grpc.AdoptionId;
        grpc.Should().Be(expected, "識別子以外は同じ（識別子は採番）");
        Guid.TryParse(grpc.AdoptionId, out _).Should().BeTrue();
        (grpc.Actor, grpc.LedgerQuantityBefore, grpc.LedgerQuantityAfter, grpc.Market).Should().Be(("owner-a", 100, 0, Proto.Market.UnitedStates));
        grpc.HasRealizedPnlRecorded.Should().BeTrue("実現損益を記録していない（false）ことも値として運ぶ");

        // 2 回目（乖離はもう無い）＝ REST の 422 と同じ文言の FAILED_PRECONDITION（二重には取り込まない）。
        var (status, error) = await PostAsync(Rest(restHost, name: null), "/risk-controls/position-drift/adopt",
            new { symbol = "AAPL", market = (int)Market.UnitedStates, reason = "再送", onBehalfOf = "owner-a" });
        var again = await FailsAsync(async () => await Grpc(grpcHost, name: null).AdoptPositionDriftAsync(new Proto.DriftAdoptionCommandRequest
        {
            Symbol = "AAPL",
            Market = Proto.Market.UnitedStates,
            Reason = "再送",
            OnBehalfOf = "owner-a",
        }));
        status.Should().Be(422);
        (again.StatusCode, again.Status.Detail).Should().Be((StatusCode.FailedPrecondition, error!));
    }

    [Fact]
    public async Task T_10_1732_乖離の取り込みの市場の未指定は_REST_の市場の省略と同じ_INVALID_ARGUMENT()
    {
        await using var baseFactory = new RiskWorkerWebApplicationFactory();
        var factory = Trusted(baseFactory);

        var (status, error) = await PostAsync(Rest(factory, name: null), "/risk-controls/position-drift/adopt",
            new { symbol = "AAPL", reason = "r", onBehalfOf = "owner-a" });
        var ex = await FailsAsync(async () => await Grpc(factory, name: null).AdoptPositionDriftAsync(
            new Proto.DriftAdoptionCommandRequest { Symbol = "AAPL", Reason = "r", OnBehalfOf = "owner-a" }));

        status.Should().Be(400);
        (ex.StatusCode, ex.Status.Detail).Should().Be((StatusCode.InvalidArgument, error!), "未指定を日本と読まない");
    }

    // ---- 監査の発行（PR #1070 の監査の指摘 1）: gRPC 面から呼んでも REST と同じ内容の監査イベントを発行する ----
    // 🔴 観測は Wolverine の送信の記録（ExecuteAndWaitForTestAsync）。処理関数の bus を飛ばす変異（監査を発行しない）をここで止める。

    private static void SeedViolations(WebApplicationFactory<Program> factory, params string[] orderIds)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IGoodFaithViolationStore>();
        foreach (var id in orderIds)
        {
            store.Append(new GoodFaithViolationRecord(
                Guid.NewGuid(), id, Guid.NewGuid(), "AAPL", Market.UnitedStates,
                PurchaseAmountInBase: 1000m, SettledCashInBase: 0m,
                OccurredOn: new DateOnly(2026, 8, 8),
                ExecutedAt: DateTimeOffset.UtcNow, RecordedAt: DateTimeOffset.UtcNow));
        }
    }

    private static void SeedBacktestPassed(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IStagePerformanceStore>().Save(new StagePerformance { BacktestPassed = true });
    }

    private static async Task<T[]> PublishedAsync<T>(WebApplicationFactory<Program> factory, Func<Task> act)
    {
        var session = await factory.Services.ExecuteAndWaitForTestAsync(act);
        return [.. session.Sent.MessagesOf<T>()];
    }

    [Fact]
    public async Task T_10_1732_gRPC_から呼んでも_REST_と同じ内容の監査イベントを発行する()
    {
        await using var restBase = new RiskWorkerWebApplicationFactory();
        await using var grpcBase = new RiskWorkerWebApplicationFactory();
        var restHost = Trusted(restBase);
        var grpcHost = Trusted(grpcBase);
        foreach (var host in new[] { restHost, grpcHost })
        {
            SeedBacktestPassed(host);
            SeedReportedDrift(host);
            SeedViolations(host, "ord-1", "ord-2");
        }

        // 段階遷移（StageTransitioned）: 承認者＝代理される利用者・認可の主体＝ボットの機密クライアント。
        var restStage = await PublishedAsync<StageTransitioned>(restHost, async () =>
            await Rest(restHost, name: null).PostAsJsonAsync("/risk-controls/stage-gate/transition", new { targetStage = 1, onBehalfOf = "owner-a" }));
        var grpcStage = await PublishedAsync<StageTransitioned>(grpcHost, async () =>
            await Grpc(grpcHost, name: null).RequestStageTransitionAsync(new Proto.StageTransitionApprovalRequest
            {
                TargetStage = Proto.TradingStage.Stage1Simulate,
                OnBehalfOf = "owner-a",
            }));
        var r = restStage.Should().ContainSingle().Subject;
        var g = grpcStage.Should().ContainSingle("gRPC 面からも監査へ発行する").Subject;
        (g.FromStage, g.ToStage, g.Kind, g.ApprovedBy, g.Reason, g.Stage1MinimumTradeCount, g.Stage1BelowStatisticalBasis, g.AuthorizedBy)
            .Should().Be((r.FromStage, r.ToStage, r.Kind, r.ApprovedBy, r.Reason, r.Stage1MinimumTradeCount, r.Stage1BelowStatisticalBasis, r.AuthorizedBy));
        (g.ApprovedBy, g.AuthorizedBy).Should().Be(("owner-a", Bot));

        // 乖離の取り込み（PositionDriftAdopted）。
        var restAdopted = await PublishedAsync<PositionDriftAdopted>(restHost, async () =>
            await Rest(restHost, name: null).PostAsJsonAsync("/risk-controls/position-drift/adopt",
                new { symbol = "AAPL", market = (int)Market.UnitedStates, reason = "アプリで売却", onBehalfOf = "owner-a" }));
        var grpcAdopted = await PublishedAsync<PositionDriftAdopted>(grpcHost, async () =>
            await Grpc(grpcHost, name: null).AdoptPositionDriftAsync(new Proto.DriftAdoptionCommandRequest
            {
                Symbol = "AAPL",
                Market = Proto.Market.UnitedStates,
                Reason = "アプリで売却",
                OnBehalfOf = "owner-a",
            }));
        var ra = restAdopted.Should().ContainSingle().Subject;
        var ga = grpcAdopted.Should().ContainSingle("gRPC 面からも監査へ発行する").Subject;
        (ga.Symbol, ga.Market, ga.LedgerQuantityBefore, ga.LedgerQuantityAfter, ga.BrokerQuantity, ga.Actor, ga.Reason, ga.AuthorizedBy)
            .Should().Be((ra.Symbol, ra.Market, ra.LedgerQuantityBefore, ra.LedgerQuantityAfter, ra.BrokerQuantity, ra.Actor, ra.Reason, ra.AuthorizedBy));
        (ga.Actor, ga.AuthorizedBy).Should().Be(("owner-a", Bot));

        // GFV の解除（GoodFaithViolationsCleared）: 解除者はトークンの名前。
        var restCleared = await PublishedAsync<GoodFaithViolationsCleared>(restHost, async () =>
            await Rest(restHost).PostAsJsonAsync("/risk-controls/good-faith-violations/clear", new { reason = "是正済み" }));
        var grpcCleared = await PublishedAsync<GoodFaithViolationsCleared>(grpcHost, async () =>
            await Grpc(grpcHost).ClearGoodFaithViolationsAsync(new Proto.GoodFaithViolationClearanceRequest { Reason = "是正済み" }));
        var rc = restCleared.Should().ContainSingle().Subject;
        var gc = grpcCleared.Should().ContainSingle("gRPC 面からも監査へ発行する").Subject;
        (gc.ClearedBy, gc.Reason, gc.RemainingCount).Should().Be((rc.ClearedBy, rc.Reason, rc.RemainingCount));
        gc.ClearedOrderIds.Should().BeEquivalentTo(rc.ClearedOrderIds).And.HaveCount(2);
    }

    // ---- 信頼一覧に呼び出し元が無い（PR #1070 の監査の指摘 2）: on_behalf_of を操作者にしない（REST と同じ） ----
    // 🔴 門（GrpcOwnerOnly）は通るが、代理を信じるのは `*:DelegatedActor:TrustedClientIds` に載ったクライアントだけである。
    // 他の試験のホストは全件 Trusted(...) なので、on_behalf_of を無条件に信じる変異はここでしか止まらない。

    [Fact]
    public async Task T_10_1732_信頼一覧に無ければ_on_behalf_of_を承認者_操作者にしない()
    {
        await using var restHost = new RiskWorkerWebApplicationFactory();
        await using var grpcHost = new RiskWorkerWebApplicationFactory();
        foreach (var host in new[] { restHost, grpcHost })
        {
            SeedBacktestPassed(host);
            SeedReportedDrift(host);
        }

        var restStage = await PublishedAsync<StageTransitioned>(restHost, async () =>
            await Rest(restHost).PostAsJsonAsync("/risk-controls/stage-gate/transition", new { targetStage = 1, onBehalfOf = "owner-a" }));
        var grpcStage = await PublishedAsync<StageTransitioned>(grpcHost, async () =>
            await Grpc(grpcHost).RequestStageTransitionAsync(new Proto.StageTransitionApprovalRequest
            {
                TargetStage = Proto.TradingStage.Stage1Simulate,
                OnBehalfOf = "owner-a",
            }));
        var r = restStage.Should().ContainSingle().Subject;
        var g = grpcStage.Should().ContainSingle().Subject;
        (g.ApprovedBy, g.AuthorizedBy).Should().Be((r.ApprovedBy, r.AuthorizedBy));
        g.ApprovedBy.Should().Be("risk-bot", "信頼一覧に無いクライアントの代理指定は無視し、トークンの主体を承認者にする");

        using var restAdopt = await Rest(restHost).PostAsJsonAsync("/risk-controls/position-drift/adopt",
            new { symbol = "AAPL", market = (int)Market.UnitedStates, reason = "アプリで売却", onBehalfOf = "owner-a" });
        var restAdopted = await restAdopt.Content.ReadFromJsonAsync<PositionDriftAdoptionResponse>(Web);
        var grpcAdopted = await Grpc(grpcHost).AdoptPositionDriftAsync(new Proto.DriftAdoptionCommandRequest
        {
            Symbol = "AAPL",
            Market = Proto.Market.UnitedStates,
            Reason = "アプリで売却",
            OnBehalfOf = "owner-a",
        });
        restAdopt.StatusCode.Should().Be(HttpStatusCode.OK);
        grpcAdopted.Actor.Should().Be(restAdopted!.Actor).And.Be("risk-bot", "操作者は on_behalf_of ではなくトークンの主体");
    }

    // ---- 門（GrpcOwnerOnly）: 8 rpc すべてでボットだけが通る ----

    private static Func<Task>[] AllRpcs(Proto.RiskControlsOwnerWrite.RiskControlsOwnerWriteClient c) =>
    [
        async () => await c.EngageKillSwitchAsync(new Proto.KillSwitchChangeRequest { Reason = "r" }),
        async () => await c.DisengageKillSwitchAsync(new Proto.KillSwitchChangeRequest { Reason = "r" }),
        async () => await c.PauseTradingAsync(new Proto.TradingPauseChangeRequest { Reason = "r" }),
        async () => await c.ResumeTradingAsync(new Proto.TradingPauseChangeRequest { Reason = "r" }),
        async () => await c.ClearGoodFaithViolationsAsync(new Proto.GoodFaithViolationClearanceRequest { Reason = "r" }),
        async () => await c.RequestStageTransitionAsync(new Proto.StageTransitionApprovalRequest { TargetStage = Proto.TradingStage.Stage1Simulate }),
        async () => await c.EvaluateWithdrawalAsync(new Proto.WithdrawalEvaluationRequest()),
        async () => await c.AdoptPositionDriftAsync(new Proto.DriftAdoptionCommandRequest { Symbol = "AAPL", Market = Proto.Market.UnitedStates, Reason = "r" }),
    ];

    [Theory]
    [InlineData(OwnerRole, null)]                     // azp の無い所有者（人の利用者）
    [InlineData(OwnerRole, "ai-stock-trading-dev")]
    [InlineData(OwnerRole, "bff")]
    [InlineData(OwnerRole, "AI-STOCK-TRADING-OWNER")]
    [InlineData(ServiceRole, Bot)]                    // 🔴 s2s には開かない（生成AI・自動処理が統制を解けない＝FR-10・ADR-0003）
    [InlineData(ServiceRole, "ai-stock-trading-svc")]
    public async Task T_10_1732_書き込みの面はボット以外を_PERMISSION_DENIED(string roles, string? azp)
    {
        await using var factory = new RiskWorkerWebApplicationFactory();

        foreach (var act in AllRpcs(Grpc(factory, roles, azp)))
            (await FailsAsync(act)).StatusCode.Should().Be(StatusCode.PermissionDenied);

        (await Rest(factory).GetFromJsonAsync<KillSwitchState>("/risk-controls/kill-switch", Web))!.Engaged.Should().BeFalse("拒否では状態を変えない");
    }

    [Fact]
    public async Task T_10_1732_資格情報が無ければ_UNAUTHENTICATED()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();

        foreach (var act in AllRpcs(Grpc(factory, roles: null, azp: null)))
            (await FailsAsync(act)).StatusCode.Should().Be(StatusCode.Unauthenticated);
    }
}
