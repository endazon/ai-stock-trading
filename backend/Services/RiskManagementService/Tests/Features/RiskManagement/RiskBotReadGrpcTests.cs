using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.GetRiskStatus;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace RiskManagementService.Tests;

// T-10-1728（提供側）, NFR, NFR-06, FR-10, FR-14, FR-20, MSP:ADR-0029, ADR-0047 決定 1〜3, IADR-0284 決定 5（段 5）, IADR-0449 決定 2・3, #753:
// Discord ボットが読むリスク管理の **gRPC 面**（段 5 の前半）。
//   - 新しい所有者限定の面 `RiskControlsOwnerRead/GetRiskStatus` が REST（`GET /risk-controls/status`）と**同じ値**を返し、門は `GrpcOwnerOnly`
//     （ボット可・s2s 不可・人の利用者不可）。
//   - 既存 `RiskControlsRead/GetStageGate` に足した項目が、REST と同じ評価器（`StageGateService.GetStatus`）の写しである。段 2 の読み手の
//     `current_stage` は変わらない。
// 本物の Program.cs（RiskWorkerWebApplicationFactory）で呼ぶ。🔴 呼べること自体が `MapGrpcService` の登録の証拠である（外すと UNIMPLEMENTED）。
public class RiskBotReadGrpcTests
{
    private const string OwnerRole = "trading-owner";
    private const string ServiceRole = "trading-service";
    private const string Bot = "ai-stock-trading-owner";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static GrpcChannel ChannelFor(WebApplicationFactory<Program> factory, string? roles, string? azp = null)
    {
        var handler = factory.Server.CreateHandler();
        if (roles is not null)
            handler = new RolesHeaderHandler(handler, roles, azp);
        return GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = handler });
    }

    private sealed class RolesHeaderHandler(HttpMessageHandler inner, string roles, string? azp) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation(TestAuthHandler.RolesHeader, roles);
            if (azp is not null)
                request.Headers.TryAddWithoutValidation(TestAuthHandler.AzpHeader, azp);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static HttpClient Rest(RiskWorkerWebApplicationFactory factory, string roles = OwnerRole)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    private static Proto.RiskControlsOwnerRead.RiskControlsOwnerReadClient Owner(GrpcChannel channel) => new(channel);

    [Fact]
    public async Task T_10_1728_稼働状態は_REST_と同じ値を返す()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        using var rest = Rest(factory);
        using var channel = ChannelFor(factory, OwnerRole, Bot);

        var restView = await rest.GetFromJsonAsync<RiskStatusView>("/risk-controls/status", Web);
        var grpc = await Owner(channel).GetRiskStatusAsync(new Proto.GetRiskStatusRequest());

        restView.Should().NotBeNull();
        grpc.Should().Be(RiskReadWireMapping.ToProto(restView!));
        grpc.HasKillSwitchEngaged.Should().BeTrue("false も値であり、欠落と区別して運ぶ");
        grpc.Stage.Should().Be(Proto.TradingStage.Stage0Verification, "C# の 0＝Stage 0 は線上で 1（未指定に化けない）");
        grpc.HasMaxOpenPositions.Should().BeTrue();
    }

    [Fact]
    public async Task T_10_1728_段階ゲートの現況は_REST_と同じ評価器の写しで段_2_の現段階は変わらない()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        using var rest = Rest(factory, ServiceRole);
        using var channel = ChannelFor(factory, OwnerRole, Bot);

        var restStage = await rest.GetFromJsonAsync<JsonNode>("/risk-controls/stage-gate", Web);
        var grpc = await new Proto.RiskControlsRead.RiskControlsReadClient(channel).GetStageGateAsync(new Proto.GetStageGateRequest());

        StageGateStatus status;
        using (var scope = factory.Services.CreateScope())
            status = scope.ServiceProvider.GetRequiredService<StageGateService>().GetStatus();

        grpc.Should().Be(RiskReadWireMapping.ToProto(status));
        grpc.CurrentStage.Should().Be(RiskReadWireMapping.ToProto((TradingStage)restStage!["currentStage"]!.GetValue<int>()));
        grpc.Promotion.Eligible.Should().Be(restStage["promotion"]!["eligible"]!.GetValue<bool>());
        grpc.Promotion.HasTargetStage.Should().BeTrue("Stage 0 には昇格先がある（空どうしの一致は何も証明しない）");
        grpc.Stage1Criteria.MinimumTradeCount.Should().Be(restStage["stage1Criteria"]!["minimumTradeCount"]!.GetValue<int>());
        grpc.CurrentSettings.HasCapitalCapRatio.Should().BeTrue();
    }

    // ---- 門（GrpcOwnerOnly）: ボットだけが通る ----

    [Theory]
    [InlineData(OwnerRole, null)]                     // azp の無い所有者（人の利用者）
    [InlineData(OwnerRole, "ai-stock-trading-dev")]   // 利用者の公開クライアント
    [InlineData(OwnerRole, "bff")]
    [InlineData(OwnerRole, "AI-STOCK-TRADING-OWNER")] // 変種
    [InlineData(ServiceRole, Bot)]                    // 🔴 s2s には開かない（REST の OwnerOnly と同じ）
    [InlineData(ServiceRole, "ai-stock-trading-svc")]
    public async Task T_10_1728_稼働状態の面はボット以外を_PERMISSION_DENIED(string roles, string? azp)
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        using var channel = ChannelFor(factory, roles, azp);

        var act = async () => await Owner(channel).GetRiskStatusAsync(new Proto.GetRiskStatusRequest());

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task T_10_1728_資格情報が無ければ_UNAUTHENTICATED_REST_の面は変わらない()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        using var anonymous = ChannelFor(factory, roles: null);

        var act = async () => await Owner(anonymous).GetRiskStatusAsync(new Proto.GetRiskStatusRequest());
        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);

        // REST の面: azp の無い所有者は従来どおり読める（BFF が中継する経路）・s2s は従来どおり 403。
        using var owner = Rest(factory, OwnerRole);
        (await owner.GetAsync("/risk-controls/status")).StatusCode.Should().Be(HttpStatusCode.OK);
        using var service = Rest(factory, ServiceRole);
        (await service.GetAsync("/risk-controls/status")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

// T-10-1728, NFR, IADR-0449 決定 3: 提供側の写しの**原則 A**（在る 0・false は設定し、C# の null は設定しない・列挙は名前で写す）。
public class RiskBotReadWireMappingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void T_10_1728_稼働状態の_null_は設定しない_在る_0_は設定する()
    {
        var view = new RiskStatusView(
            false, true, null, false, ActiveTradingControl.DailyLossLockout, true, TradingStage.Stage0Verification, BrokerProvider.InternalPaper,
            0m, -1.5m, -1.5m, null, 0m, null, null, 0.05m, 0.2m, 0, 5);

        var wire = RiskReadWireMapping.ToProto(view);

        wire.HasLockoutReleaseOn.Should().BeFalse("解除日が未定（null）");
        wire.HasMaxDailyOrderAmount.Should().BeFalse("上限を解決できない（null）を 0 と書かない（#990）");
        wire.HasDailyRealizedPnl.Should().BeTrue();
        wire.DailyRealizedPnl.Should().Be("0");
        wire.UnrealizedPnl.Should().Be("-1.5");
        wire.HasOpenPositionCount.Should().BeTrue("在る 0 は設定する");
        wire.Stage.Should().Be(Proto.TradingStage.Stage0Verification);

        var resolved = RiskReadWireMapping.ToProto(view with { LockoutReleaseOn = new DateOnly(2026, 9, 2), MaxDailyOrderAmount = 0m });
        resolved.LockoutReleaseOn.Should().Be("2026-09-02");
        resolved.MaxDailyOrderAmount.Should().Be("0", "在る 0（上限 0）は設定する");
    }

    [Fact]
    public void T_10_1728_段階ゲートの現況は履歴と評価と合格条件を名前で写し_null_は設定しない()
    {
        var status = new StageGateStatus(
            TradingStage.Stage3ScaledLive,
            new StageSettings(TradingStage.Stage3ScaledLive, BrokerProvider.InternalPaper, 0.5m),
            [
                new StageTransition(1, TradingStage.Stage0Verification, TradingStage.Stage1Simulate, StageTransitionKind.Promotion, "owner", T0, "昇格"),
                new StageTransition(2, TradingStage.Stage1Simulate, TradingStage.Stage1Simulate, StageTransitionKind.ShortSellReleaseVerdict, "owner", T0, "verdict"),
            ],
            new PromotionAssessment(null, false, [StageGateCriterion.BacktestNotPassed, StageGateCriterion.ControlViolationCountUnavailable]),
            new WithdrawalAssessment(false, null, false, null),
            new Stage1Progress(0, 0),
            new Stage1GateCriteria(60, 50, 120),
            new ShortSellReleaseState(ShortSellReleaseVerdictStatus.Missing, null, "fp", "strategy", false, null));

        var wire = RiskReadWireMapping.ToProto(status);

        wire.CurrentSettings.Mode.Should().Be(Proto.BrokerProvider.InternalPaper, "C# の 0 は線上で未指定に化けない");
        wire.CurrentSettings.CapitalCapRatio.Should().Be("0.5");
        wire.Promotion.HasTargetStage.Should().BeFalse("最上段（null）は設定しない");
        wire.Promotion.HasEligible.Should().BeTrue();
        wire.Promotion.UnmetCriteria.Should().Equal(
            Proto.StageGateCriterion.BacktestNotPassed, Proto.StageGateCriterion.ControlViolationCountUnavailable);
        wire.Withdrawal.HasReason.Should().BeFalse();
        wire.Withdrawal.HasProposedStage.Should().BeFalse();
        wire.Withdrawal.HasTriggered.Should().BeTrue("false も値として運ぶ");
        wire.History.Select(h => h.Kind).Should().Equal(
            Proto.StageTransitionKind.Promotion, Proto.StageTransitionKind.ShortSellReleaseVerdict);
        wire.History[0].FromStage.Should().Be(Proto.TradingStage.Stage0Verification);
        wire.History[0].OccurredAt.Should().Be("2026-09-01T00:00:00.0000000+09:00", "オフセットを保つ往復書式");
        wire.Stage1Criteria.BelowStatisticalBasis.Should().BeTrue("送り手が宣言する（50 件 < 100 件）");

        var withdrawn = RiskReadWireMapping.ToProto(status with
        {
            Promotion = new PromotionAssessment(TradingStage.Stage0Verification, true, []),
            Withdrawal = new WithdrawalAssessment(true, WithdrawalReason.DrawdownBreachedMultiple, true, TradingStage.Stage0Verification),
        });
        withdrawn.Promotion.TargetStage.Should().Be(Proto.TradingStage.Stage0Verification);
        withdrawn.Withdrawal.Reason.Should().Be(Proto.WithdrawalReason.DrawdownBreachedMultiple, "C# の 0 は線上で未指定に化けない");
        withdrawn.Withdrawal.ProposedStage.Should().Be(Proto.TradingStage.Stage0Verification);
    }

    // 🔴 列挙の全値が名前で写る（C# に値を足したら赤になる＝写しの足し忘れを止める）。
    [Fact]
    public void T_10_1728_列挙の全値が未指定以外へ写る()
    {
        Enum.GetValues<StageGateCriterion>().Select(RiskReadWireMapping.ToProto)
            .Should().NotContain(Proto.StageGateCriterion.Unspecified).And.OnlyHaveUniqueItems();
        Enum.GetValues<StageTransitionKind>().Select(RiskReadWireMapping.ToProto)
            .Should().NotContain(Proto.StageTransitionKind.Unspecified).And.OnlyHaveUniqueItems();
        Enum.GetValues<WithdrawalReason>().Select(RiskReadWireMapping.ToProto)
            .Should().NotContain(Proto.WithdrawalReason.Unspecified).And.OnlyHaveUniqueItems();
    }
}
