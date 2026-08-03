using System.Net;
using System.Net.Http.Json;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Worker.Foundation.Endpoints;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.RiskManagement.Worker.Tests;

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
            new OrderIntent(symbol, Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper,
                quantity, 20m, PositionEffect.Open, StopLossPrice: 19m, FxRateToBase: 150m),
            at);
        ledger.AppendFill(decisionId, $"open-{decisionId:N}", quantity, 20m, at);
    }

    private static object Body(string symbol, int? quantity = null, decimal? limitPrice = 21m, string? reason = "手仕舞い") =>
        new { symbol, market = (int)Market.UnitedStates, quantity, limitPrice, reason };

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
        var harness = factory.Services.GetRequiredService<ITestHarness>();

        var res = await OwnerClient().PostAsJsonAsync("/risk-controls/positions/close", Body("CLOSE1"));

        res.StatusCode.Should().Be(HttpStatusCode.Accepted, "約定は後から非同期に成立するため 202");

        var dto = await res.Content.ReadFromJsonAsync<PositionCloseResponseDto>();
        dto!.Quantity.Should().Be(100);
        dto.Side.Should().Be(TradeSide.Sell);
        dto.Price.Should().Be(21m);

        // 既存経路（発注執行・台帳・通知）へ載せる承認と、監査のための要求イベントの双方を発行する。
        (await harness.Published.Any<OrderApproved>(
            c => c.Context.Message.DecisionId == dto.DecisionId
              && c.Context.Message.Intent.PositionEffect == PositionEffect.Close)).Should().BeTrue();
        (await harness.Published.Any<PositionCloseRequested>(
            c => c.Context.Message.DecisionId == dto.DecisionId
              && c.Context.Message.Actor == "test-owner"
              && c.Context.Message.Reason == "手仕舞い")).Should().BeTrue();
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
    public async Task 価格を決められなければ422()
    {
        // 現在値キャッシュは空。limitPrice も無ければ価格 0 の注文を投げずに拒否する。
        SeedPosition("CLOSE4", 10);

        var res = await OwnerClient()
            .PostAsJsonAsync("/risk-controls/positions/close", Body("CLOSE4", limitPrice: null));

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
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
        Guid DecisionId, string Symbol, Market Market, TradeSide Side, int Quantity, decimal Price, TradeMode Mode);
}
