using System.Net;
using System.Net.Http.Json;
using AiStockTrading.MarketMonitor.Application.State;
using AiStockTrading.MarketMonitor.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.MarketMonitor.Worker.Tests;

// FR-03, FR-11, FR-13, UC-06, ADR-0007, IADR-0088: 監視銘柄（watchlist）エンドポイントの認可（owner サブグループ）・
// 追加/削除・検証(400)・変更履歴を検証する。
public class MonitorWatchlistEndpointsTests(MonitorWorkerWebApplicationFactory factory)
    : IClassFixture<MonitorWorkerWebApplicationFactory>
{
    private HttpClient OwnerClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    private static Task<HttpResponseMessage> AddAsync(HttpClient client, string symbol, Market market, string reason) =>
        client.PostAsJsonAsync("/monitor/watchlist", new { Symbol = symbol, Market = market, Reason = reason });

    private static Task<HttpResponseMessage> RemoveAsync(HttpClient client, string symbol, Market market, string reason)
    {
        // DELETE に body を持たせるため HttpRequestMessage を組む（IADR-0088）。
        var req = new HttpRequestMessage(HttpMethod.Delete, "/monitor/watchlist")
        {
            Content = JsonContent.Create(new { Symbol = symbol, Market = market, Reason = reason }),
        };
        return client.SendAsync(req);
    }

    [Fact]
    public async Task 未認証の監視銘柄取得は401()
    {
        var res = await factory.CreateClient().GetAsync("/monitor/watchlist");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 利用者ロールを持たない場合は403()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "viewer");

        var res = await client.GetAsync("/monitor/watchlist");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 非owner_の追加は403()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "viewer");

        var res = await AddAsync(client, "AAPL", Market.UnitedStates, "理由");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // FR-02, IADR-0095: 読み取り（GET /watchlist）はサービス（trading-service）でも s2s 照会できる（OwnerOrService）。
    // 定時サイクル（#11 TradeDecision）が権威源から監視銘柄を取得するための開放。
    [Fact]
    public async Task サービスロールは監視銘柄を取得できる()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-service");

        var res = await client.GetAsync("/monitor/watchlist");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // FR-02, IADR-0095: 変更（追加）はサービスに許さない（OwnerOnly 据え置き・変更は利用者のみ・ADR-0007）。
    [Fact]
    public async Task サービスロールの追加は403()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-service");

        var res = await AddAsync(client, "AAPL", Market.UnitedStates, "理由");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 利用者は監視銘柄を追加でき取得と履歴に反映される()
    {
        var client = OwnerClient();

        var add = await AddAsync(client, "NVDA", Market.UnitedStates, "半導体の監視開始");
        add.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await client.GetFromJsonAsync<List<MonitoredSymbol>>("/monitor/watchlist");
        list.Should().Contain(s => s.Symbol == "NVDA" && s.Market == Market.UnitedStates);

        var history = await client.GetFromJsonAsync<List<HistoryDto>>("/monitor/watchlist/history");
        history.Should().Contain(e =>
            e.ChangeType == MonitorSettingsChangeType.WatchlistSymbolAdded && e.Reason == "半導体の監視開始");
    }

    [Fact]
    public async Task 利用者は監視銘柄を削除でき取得から消える()
    {
        var client = OwnerClient();
        await AddAsync(client, "TSLA", Market.UnitedStates, "追加");

        var remove = await RemoveAsync(client, "TSLA", Market.UnitedStates, "監視終了");
        remove.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await client.GetFromJsonAsync<List<MonitoredSymbol>>("/monitor/watchlist");
        list.Should().NotContain(s => s.Symbol == "TSLA");

        var history = await client.GetFromJsonAsync<List<HistoryDto>>("/monitor/watchlist/history");
        history.Should().Contain(e =>
            e.ChangeType == MonitorSettingsChangeType.WatchlistSymbolRemoved && e.Reason == "監視終了");
    }

    [Fact]
    public async Task 理由が空の追加は400()
    {
        var res = await AddAsync(OwnerClient(), "AAPL", Market.UnitedStates, "");
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task 重複追加は400()
    {
        var client = OwnerClient();
        await AddAsync(client, "AMZN", Market.UnitedStates, "初回");

        var dup = await AddAsync(client, "AMZN", Market.UnitedStates, "重複");

        dup.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task 不在銘柄の削除は400()
    {
        var res = await RemoveAsync(OwnerClient(), "NOPE", Market.UnitedStates, "不在");
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task marketを省略した追加は400()
    {
        // 非 nullable enum の既定値 0（Japan）へ暗黙バインドされず、明示指定を必須にする（IADR-0088・AI レビュー対応）。
        var res = await OwnerClient().PostAsJsonAsync("/monitor/watchlist", new { Symbol = "AAPL", Reason = "market 省略" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed record HistoryDto(string Actor, MonitorSettingsChangeType ChangeType, string Reason);
}
