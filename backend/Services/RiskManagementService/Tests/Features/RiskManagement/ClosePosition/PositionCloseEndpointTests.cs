using System.Net;
using System.Net.Http.Json;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.ClosePosition;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, FR-11, UC-06, ADR-0003, #292, IADR-0117: 建玉の手仕舞いエンドポイント。
// 認可（OwnerOnly）・入力検証・HTTP 写像・発行イベント、そして「統制で止まらない」ことを固定する。
public class PositionCloseEndpointTests(RiskWorkerWebApplicationFactory factory)
    : IClassFixture<RiskWorkerWebApplicationFactory>
{
    private const string Owner = "trading-owner";
    private const string Service = "trading-service";

    private HttpClient OwnerClient() => ClientWithRoles(Owner);

    private HttpClient ClientWithRoles(string roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    // 台帳へ建玉（承認＋約定）を積む。クラス内のテストは同一 InMemory DB を共有するため銘柄で隔離する。
    private void SeedPosition(string symbol, int quantity)
    {
        using var scope = factory.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IPortfolioLedgerStore>();
        var decisionId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        ledger.AppendApproval(
            decisionId,
            new OrderIntent(symbol, Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
                quantity, 20m, PositionEffect.Open, StopLossPrice: 19m, FxRateToBase: 150m),
            at);
        ledger.AppendFill(decisionId, $"open-{decisionId:N}", quantity, 20m, at);
    }

    private static object Body(
        string symbol, int? quantity = null, decimal? limitPrice = 21m, string? reason = "手仕舞い",
        bool? marketOrder = null) =>
        new { symbol, market = (int)Market.UnitedStates, quantity, limitPrice, reason, marketOrder };

    [Fact]
    public async Task 未認証は401()
    {
        var res = await factory.CreateClient().PostAsJsonAsync("/risk-controls/positions/close", Body("AUTH1"));

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task サービスロールでは403()
    {
        // 最小権限（ADR-0003・IADR-0051）: 建玉を落とす操作はサービス（生成AI・自動処理）へ開かない。
        var res = await ClientWithRoles(Service)
            .PostAsJsonAsync("/risk-controls/positions/close", Body("AUTH2"));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 利用者は建玉を全量決済できる()
    {
        SeedPosition("CLOSE1", 100);

        // ADR-0013, IADR-0129, #354: MassTransit の ITestHarness に代えて Wolverine.Tracking で発行を捕捉する。
        HttpResponseMessage res = null!;
        var session = await factory.Services.ExecuteAndWaitAsync(async () =>
        {
            res = await OwnerClient().PostAsJsonAsync("/risk-controls/positions/close", Body("CLOSE1"));
        });

        res.StatusCode.Should().Be(HttpStatusCode.Accepted, "約定は後から非同期に成立するため 202");

        var dto = await res.Content.ReadFromJsonAsync<PositionCloseResponseDto>();
        dto!.Quantity.Should().Be(100);
        dto.Side.Should().Be(TradeSide.Sell);
        dto.Price.Should().Be(21m);

        // 既存経路（発注執行・台帳・通知）へ載せる承認と、監査のための要求イベントの双方を発行する。
        session.Sent.MessagesOf<OrderApproved>().Should().Contain(m =>
            m.DecisionId == dto.DecisionId
              && m.Intent.PositionEffect == PositionEffect.Close);
        session.Sent.MessagesOf<PositionCloseRequested>().Should().Contain(m =>
            m.DecisionId == dto.DecisionId
              && m.Actor == "test-owner"
              && m.Reason == "手仕舞い");
    }

    [Fact]
    public async Task 部分決済できる()
    {
        SeedPosition("CLOSE2", 100);

        var res = await OwnerClient()
            .PostAsJsonAsync("/risk-controls/positions/close", Body("CLOSE2", quantity: 30));

        res.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await res.Content.ReadFromJsonAsync<PositionCloseResponseDto>())!.Quantity.Should().Be(30);
    }

    [Fact]
    public async Task 建玉が無ければ404()
    {
        var res = await OwnerClient()
            .PostAsJsonAsync("/risk-controls/positions/close", Body("NOTHELD"));

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task 保有を超える数量は422()
    {
        SeedPosition("CLOSE3", 10);

        var res = await OwnerClient()
            .PostAsJsonAsync("/risk-controls/positions/close", Body("CLOSE3", quantity: 11));

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task 指値を選んだのに価格を決められなければ422()
    {
        // 現在値キャッシュは空。**指値を明示的に選んだ**（marketOrder=false）のに価格が無ければ、
        // 価格 0 の指値を投げずに拒否する。#847 以降、省略時は成行になるためここへは落ちない。
        SeedPosition("CLOSE4", 10);

        var res = await OwnerClient().PostAsJsonAsync(
            "/risk-controls/positions/close", Body("CLOSE4", limitPrice: null, marketOrder: false));

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // 🔴 #847, IADR-0357: 現在値が取れなくても**成行の手仕舞いは通る**（市況フィードの不調で手仕舞えなくしない）。
    [Fact]
    public async Task 現在値が無くても成行の手仕舞いは受理される()
    {
        SeedPosition("CLOSE9", 10);

        var res = await OwnerClient()
            .PostAsJsonAsync("/risk-controls/positions/close", Body("CLOSE9", limitPrice: null));

        res.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    // #847, IADR-0357: 成行と指値の同時指定は矛盾であり 400（黙ってどちらかを捨てない）。
    [Fact]
    public async Task 成行と指値の同時指定は400()
    {
        SeedPosition("CLOSE10", 10);

        var res = await OwnerClient().PostAsJsonAsync(
            "/risk-controls/positions/close", Body("CLOSE10", limitPrice: 21m, marketOrder: true));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task 理由が無ければ400()
    {
        var res = await OwnerClient()
            .PostAsJsonAsync("/risk-controls/positions/close", Body("CLOSE5", reason: " "));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task 市場の省略は400()
    {
        // 非 nullable enum なら省略時に既定値 0（＝Japan）へ暗黙束縛され、意図しない市場の建玉を対象にしてしまう。
        var res = await OwnerClient().PostAsJsonAsync(
            "/risk-controls/positions/close",
            new { symbol = "CLOSE6", quantity = (int?)null, limitPrice = 21m, reason = "手仕舞い" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task 銘柄の省略は400()
    {
        var res = await OwnerClient().PostAsJsonAsync(
            "/risk-controls/positions/close",
            new { market = (int)Market.UnitedStates, limitPrice = 21m, reason = "手仕舞い" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task kill_switch_起動中でも手仕舞いできる()
    {
        // FR-10 本文: 「kill switch・日次損失ロックアウト・一時停止はいずれも手仕舞い（Close）と損切りは止めない」。
        SeedPosition("CLOSE7", 50);
        var client = OwnerClient();
        (await client.PostAsJsonAsync("/risk-controls/kill-switch/engage",
            new KillSwitchRequest("検証のため全停止"))).StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await client.PostAsJsonAsync("/risk-controls/positions/close", Body("CLOSE7"));

        res.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // 後続テストへ影響させないため解除する（kill switch は永続状態）。
        await client.PostAsJsonAsync("/risk-controls/kill-switch/disengage", new KillSwitchRequest("検証終了"));
    }

    [Fact]
    public async Task 一時停止中でも手仕舞いできる()
    {
        SeedPosition("CLOSE8", 50);
        var client = OwnerClient();
        (await client.PostAsJsonAsync("/risk-controls/pause",
            new PauseRequest("検証のため一時停止"))).StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await client.PostAsJsonAsync("/risk-controls/positions/close", Body("CLOSE8"));

        res.StatusCode.Should().Be(HttpStatusCode.Accepted);

        await client.PostAsJsonAsync("/risk-controls/resume", new PauseRequest("検証終了"));
    }

    [Fact]
    public void 決済サービスは統制ストアに依存しない()
    {
        // 「手仕舞いを止めない」の構造的な保証。統制ストアを注入していれば、いずれ判定に混入し得る。
        var dependencies = typeof(PositionCloseService)
            .GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();

        dependencies.Should().NotContain(typeof(IKillSwitchStore));
        dependencies.Should().NotContain(typeof(ILockoutStore));
        dependencies.Should().NotContain(typeof(IPauseStore));
    }

    // 応答 DTO（camelCase・列挙は数値で往復する）。
    private sealed record PositionCloseResponseDto(
        Guid DecisionId, string Symbol, Market Market, TradeSide Side, int Quantity, decimal Price, BrokerProvider Mode);
}
