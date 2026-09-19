using System.Net;
using System.Net.Http.Json;
using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Tests;

// 🔴 T-10-586, FR-05, FR-10, FR-11, UC-06, #847, #768, IADR-0357: 板に残った手仕舞いを取り消すエンドポイント。
// 利用者専用（OwnerOnly）・理由必須・監査に「誰が・なぜ」が残ること、そして**アプリ操作に逃がさない**こと。
public class PositionCloseCancellationEndpointTests(RiskWorkerWebApplicationFactory factory)
    : IClassFixture<RiskWorkerWebApplicationFactory>
{
    private const string Owner = "trading-owner";
    private const string Service = "trading-service";
    private const string Path = "/risk-controls/positions/close/cancel";

    private HttpClient OwnerClient() => ClientWithRoles(Owner);

    private HttpClient ClientWithRoles(string roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    // 台帳へ「未約定の手仕舞い承認」を積む（発注執行が発注済み・板に残っている状態に相当）。
    private Guid SeedPendingClose(string symbol, PositionEffect effect = PositionEffect.Close)
    {
        using var scope = factory.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IPortfolioLedgerStore>();
        var decisionId = Guid.NewGuid();
        ledger.AppendApproval(
            decisionId,
            new OrderIntent(symbol, Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                BrokerProvider.MoomooSimulate, 3381, 334.09m, effect),
            DateTimeOffset.UtcNow.AddMinutes(-5));
        return decisionId;
    }

    private static object Body(Guid decisionId, string? reason = "指値が置いていかれたので取り消す") =>
        new { decisionId, reason };

    [Fact]
    public async Task 未認証は401()
    {
        var res = await factory.CreateClient().PostAsJsonAsync(Path, Body(Guid.NewGuid()));

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task サービスロールでは403()
    {
        // 最小権限（ADR-0003）: 注文を取り消す操作は生成AI・自動処理へ開かない。
        var res = await ClientWithRoles(Service).PostAsJsonAsync(Path, Body(Guid.NewGuid()));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 利用者は板に残った手仕舞いを取り消せる()
    {
        var decisionId = SeedPendingClose("XCAN1");

        HttpResponseMessage res = null!;
        var session = await factory.Services.ExecuteAndWaitAsync(async () =>
        {
            res = await OwnerClient().PostAsJsonAsync(Path, Body(decisionId));
        });

        res.StatusCode.Should().Be(
            HttpStatusCode.Accepted, "取消がブローカーへ届くのは非同期であるため 202（200 ではない）");

        // FR-11: 「誰が・なぜ」が監査台帳へ残る。
        session.Sent.MessagesOf<PositionCloseCancellationRequested>().Should().Contain(m =>
            m.DecisionId == decisionId
              && m.Actor == "test-owner"
              && m.Reason == "指値が置いていかれたので取り消す");
    }

    [Fact]
    public async Task 理由が無ければ400()
    {
        var res = await OwnerClient().PostAsJsonAsync(Path, Body(Guid.NewGuid(), reason: " "));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task 判断IDが無ければ400()
    {
        var res = await OwnerClient().PostAsJsonAsync(Path, new { reason = "理由" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task 台帳に無い注文は404()
    {
        var res = await OwnerClient().PostAsJsonAsync(Path, Body(Guid.NewGuid()));

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task 手仕舞い以外の注文は422()
    {
        var decisionId = SeedPendingClose("XCAN2", PositionEffect.Open);

        var res = await OwnerClient().PostAsJsonAsync(Path, Body(decisionId));

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }
}
