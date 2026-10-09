using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using NotificationService.Features.Notifications;
using NotificationService.Infrastructure.ExternalServices;
using Xunit;
using MonitorProto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;
using ReportProto = AiStockTrading.Shared.Grpc.Report.V1;
using RiskProto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace NotificationService.Tests;

// T-10-1736, NFR, FR-14, MSP:ADR-0029, ADR-0047 決定 1・2, IADR-0284 決定 5（段 5）, IADR-0450 決定 4・5, #753:
// **本番の Program.cs の組み立て**で、`*:Grpc` を宣言すればボットの**書き込み 13 本**も gRPC 実装（ボットの owner トークン）で呼ばれ、
// 🔴 **gRPC が失敗しても REST の名前付きクライアントへ黙って落ちない**こと、宣言が無ければ従来どおり REST であること、方針の改訂の deadline の構成、
// 既定の配備が新しい構成キーを置かないことを固定する。
public class BotWriteGrpcWiringTests
{
    private static readonly string[] RestClients =
    [
        "risk-kill-switch", "risk-pause", "risk-stage-gate", "risk-good-faith-violations", "risk-position-drift",
        "report-review", "report-policy-revision", "market-monitor-watchlist",
    ];

    // REST の名前付きクライアントの最下層を数える（呼ばれたら REST へ落ちた証拠）。
    private sealed class CountingHandler(ConcurrentQueue<string> calls) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            calls.Enqueue(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private static Dictionary<string, string> Declared(BotReadGrpcStubHost host) => new()
    {
        ["RiskManagement:Grpc"] = host.Address,
        ["Reports:Grpc"] = host.Address,
        ["MarketMonitor:Grpc"] = host.Address,
        ["RiskManagement:GrpcMaxAttempts"] = "3",   // 読み取りだけに効く（書き込みは再試行しない）
        ["Notifications:Discord:OwnerAuth:TokenEndpoint"] = host.TokenEndpoint,
        ["Notifications:Discord:OwnerAuth:ClientId"] = "ai-stock-trading-owner",
        ["Notifications:Discord:OwnerAuth:ClientSecret"] = "test-only",
    };

    private static Action<IServiceCollection> CountRest(ConcurrentQueue<string> calls) => services =>
    {
        foreach (var name in RestClients)
            services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new CountingHandler(calls));
    };

    private static async Task CallAllWritesAsync(IServiceProvider sp)
    {
        await sp.GetRequiredService<IKillSwitchController>().EngageAsync("r");
        await sp.GetRequiredService<IKillSwitchController>().DisengageAsync("r");
        await sp.GetRequiredService<IPauseController>().PauseAsync("r");
        await sp.GetRequiredService<IPauseController>().ResumeAsync("r");
        await sp.GetRequiredService<IGoodFaithViolationController>().ClearAsync("r");
        await sp.GetRequiredService<IStageGateController>().RequestTransitionAsync(1, "owner-a");
        await sp.GetRequiredService<IStageGateController>().EvaluateWithdrawalAsync();
        await sp.GetRequiredService<IPositionDriftAdoptionController>().AdoptAsync("AAPL", AiStockTrading.Shared.Contracts.Trading.Market.UnitedStates, "r", "owner-a");
        await sp.GetRequiredService<IReportReviewController>().ConfirmAsync("daily-2026-09-01", 1, "owner-a");
        await sp.GetRequiredService<IReportReviewController>().RequestChangesAsync("daily-2026-09-01", 1);
        await sp.GetRequiredService<IPolicyRevisionController>().ReviseAsync(null, "i", "owner-a", null);
        await sp.GetRequiredService<IPolicyRevisionController>().RecordWatchlistApplyAsync(Guid.NewGuid(), "applied", [], "m", "owner-a");
        await sp.GetRequiredService<IMarketMonitorWatchlistController>().ApplyProposalAsync(
            [], [new WatchlistChangeSuggestionView("add", "NVDA", "r")], "ref", "owner-a");
    }

    [Fact]
    public async Task T_10_1736_宣言すれば書き込みも_gRPC_実装になりボットの_owner_トークンで呼び_REST_を呼ばない()
    {
        var behavior = new BotReadStubBehavior();
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        var restCalls = new ConcurrentQueue<string>();
        using var factory = new BotReadGrpcWiringTests.Factory(Declared(host), CountRest(restCalls));
        _ = factory.CreateClient();
        var sp = factory.Services;

        sp.GetRequiredService<IKillSwitchController>().Should().BeOfType<GrpcKillSwitchController>();
        sp.GetRequiredService<IGoodFaithViolationController>().Should().BeOfType<GrpcGoodFaithViolationController>();
        sp.GetRequiredService<IPositionDriftAdoptionController>().Should().BeOfType<GrpcPositionDriftAdoptionController>();

        await CallAllWritesAsync(sp);

        behavior.Received.Select(r => r.Rpc).Should().BeEquivalentTo(GrpcBotWritesTests.WriteRpcs);
        behavior.Received.Should().OnlyContain(r => r.Authorization == $"Bearer {behavior.IssuedToken}",
            "メタデータにはボットの owner マップ機密クライアントのトークンを載せる（ADR-0047 決定 2）");
        restCalls.Should().BeEmpty("宣言があれば書き込みも REST を呼ばない");
    }

    // 🔴 gRPC が失敗しても（提供側が古い・不達）REST へ黙って落ちない。落とすと「時間切れ＝実は適用済み」を REST で再実行することになる。
    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.Unimplemented)]
    public async Task T_10_1736_gRPC_が失敗しても_REST_へ落とさず再試行もしない(StatusCode status)
    {
        var behavior = new BotReadStubBehavior
        {
            KillSwitch = BotReadStubBehavior.Fails<RiskProto.KillSwitchChangeResponse>(status),
            Pause = BotReadStubBehavior.Fails<RiskProto.TradingPauseChangeResponse>(status),
            GoodFaith = BotReadStubBehavior.Fails<RiskProto.GoodFaithViolationClearanceResponse>(status),
            Transition = BotReadStubBehavior.Fails<RiskProto.StageTransitionApprovalResponse>(status),
            Withdrawal = BotReadStubBehavior.Fails<RiskProto.WithdrawalEvaluationResponse>(status),
            Adoption = BotReadStubBehavior.Fails<RiskProto.DriftAdoptionCommandResponse>(status),
            Confirm = BotReadStubBehavior.Fails<ReportProto.ReportConfirmationResponse>(status),
            Changes = BotReadStubBehavior.Fails<ReportProto.ReportChangesResponse>(status),
            Revision = BotReadStubBehavior.Fails<ReportProto.PolicyRevisionProposalResponse>(status),
            ApplyRecord = BotReadStubBehavior.Fails<ReportProto.WatchlistApplyRecordResponse>(status),
            Apply = BotReadStubBehavior.Fails<MonitorProto.WatchlistProposalApplicationResponse>(status),
        };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        var restCalls = new ConcurrentQueue<string>();
        using var factory = new BotReadGrpcWiringTests.Factory(Declared(host), CountRest(restCalls));
        _ = factory.CreateClient();

        await CallAllWritesAsync(factory.Services);

        restCalls.Should().BeEmpty("失敗しても REST の実装へ落とさない");
        foreach (var rpc in GrpcBotWritesTests.WriteRpcs)
            behavior.Calls(rpc).Should().Be(1, $"{rpc} は宣言した試行回数（3）でも再試行しない");
    }

    // 陰性対照: 宣言が無ければ、書き込みの 8 ポートは従来どおり REST（輸送そのものが登録されない）。
    [Fact]
    public async Task T_10_1736_宣言が無ければ書き込みも_REST_のまま()
    {
        var restCalls = new ConcurrentQueue<string>();
        using var factory = new BotReadGrpcWiringTests.Factory([], CountRest(restCalls));
        _ = factory.CreateClient();
        var sp = factory.Services;

        sp.GetRequiredService<IKillSwitchController>().Should().BeOfType<HttpKillSwitchController>();
        sp.GetRequiredService<IGoodFaithViolationController>().Should().BeOfType<HttpGoodFaithViolationController>();
        sp.GetRequiredService<IPositionDriftAdoptionController>().Should().BeOfType<HttpPositionDriftAdoptionController>();
        sp.GetRequiredService<IPauseController>().Should().BeOfType<HttpPauseController>();

        await CallAllWritesAsync(sp);

        restCalls.Should().HaveCount(13, "13 本の書き込みがすべて REST を呼ぶ（空どうしの一致は何も証明しない）");
    }

    // 方針の改訂・適用の内訳の記録の deadline は REST の 120 秒のクライアントと同値（未設定）。宣言すれば従う。照会・確定の deadline とは別。
    // ［2026-10-10 / #243・IADR-0522 の追記］既定を 90 → 120 秒（報告書サービス側の上限を 60 → 95 秒へ上げたため。外側＞内側は T-10-2507）。
    [Theory]
    [InlineData(null, 120)]
    [InlineData("7", 7)]
    public void T_10_1736_方針の改訂の_deadline_は_REST_と同じ既定で構成に従う(string? seconds, int expected)
    {
        var settings = new Dictionary<string, string> { ["Reports:Grpc"] = "http://report-service:8081" };
        if (seconds is not null)
            settings["Reports:GrpcPolicyRevisionTimeoutSeconds"] = seconds;
        using var factory = new BotReadGrpcWiringTests.Factory(settings);
        _ = factory.CreateClient();

        var transport = factory.Services.GetRequiredService<ReportsGrpcTransport>();
        transport.PolicyRevisionCalls.Timeout.Should().Be(TimeSpan.FromSeconds(expected));
        transport.Calls.Timeout.Should().Be(TimeSpan.FromSeconds(5), "照会・確定・差し戻しは REST の report-review と同じ 5 秒");
        transport.PolicyRevisionCalls.MaxAttempts.Should().Be(1);
    }

    // 🔴 配備の既定は REST。新しい構成キーも既定の配備に置かない（helm の既定・values-local・compose）。
    [Theory]
    [InlineData("deploy/helm/ai-stock-trading/values.yaml", "notification")]
    [InlineData("deploy/helm/ai-stock-trading/values-local.yaml", "notification")]
    [InlineData("docker-compose.yml", "notification-service")]
    public void T_10_1736_既定の配備は方針の改訂の_deadline_も置かない(string path, string service)
    {
        var block = BotReadGrpcWiringTests.ServiceBlock(
            File.ReadAllText(Path.Combine([BotReadGrpcWiringTests.RepoRoot(), .. path.Split('/')])), service);

        block.Should().NotBeEmpty();
        Regex.IsMatch(block, @"(?m)^(?!\s*#).*Reports__GrpcPolicyRevisionTimeoutSeconds").Should().BeFalse();
    }
}
