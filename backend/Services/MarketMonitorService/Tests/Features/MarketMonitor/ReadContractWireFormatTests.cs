using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using MarketMonitorService.Features.MarketMonitor;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketMonitorService.Tests;

// 🔴 T-10-931, FR-02, FR-13, #957, IADR-0095, IADR-0408（2026-09-25 追記。リスク管理の T-10-805 の同型）: **本番の Program.cs が
// `GET /monitor/watchlist`（判断の定時サイクルが読む口）に出す JSON は、応答型 `MonitoredSymbol` の一覧を web 既定（camelCase・
// 列挙は数値）で直列化したものと一字一句同じである**ことを固定する。受け手（判断 T-10-930）はこの設定で直列化した本文を読ませており、
// その前提（＝送り手が JSON 設定を変えていない）は受け手の側からは見えない —— 例えば市場監視の Program.cs に
// `JsonStringEnumConverter` が足されると、受け手のテストは緑のまま実行時は市場の読み取りで例外（＝既定 watchlist へ倒れる）になる。
// 受け手が使う `symbol`（文字列）と `market`（数値）が載っていることも名指しで表明する。
public class ReadContractWireFormatTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task 監視銘柄の本文は応答型を_web_既定で直列化したものと同じ()
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<MonitorWatchlistService>()
                .Add("AAPL", Market.UnitedStates, "test-owner", "契約の固定");
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-service");
        var res = await client.GetAsync("/monitor/watchlist");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsArray();

        using var s = factory.Services.CreateScope();
        var expected = s.ServiceProvider.GetRequiredService<MonitorWatchlistService>().GetWatchlist();
        // 空の配列どうしの一致は何も証明しない。
        expected.Should().Contain(m => m.Symbol == "AAPL" && m.Market == Market.UnitedStates);

        JsonNode.DeepEquals(body, JsonSerializer.SerializeToNode(expected, Web))
            .Should().BeTrue($"watchlist の本文が web 既定と異なる: {body.ToJsonString()}");
        var aapl = body.Single(n => n!["symbol"]!.GetValue<string>() == "AAPL")!;
        aapl["market"]!.GetValue<int>().Should().Be((int)Market.UnitedStates);
    }
}
