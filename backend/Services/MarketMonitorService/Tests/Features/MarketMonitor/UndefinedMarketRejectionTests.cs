using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using MarketMonitorService.Domain;
using MarketMonitorService.Features.MarketMonitor;
using MarketMonitorService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketMonitorService.Tests;

// T-10-1720, T-10-1721, FR-02, FR-13, IADR-0447（2026-09-27 追記）, #1065 F1:
// 監視銘柄の**入口で**未定義の市場を拒む。1 件の追加（POST /monitor/watchlist）は拒否していたが、全置換（PUT /monitor/settings）は
// `"market":7` を `(Market)7` のまま保存し、`GET /monitor/watchlist` が 7 を返していた。初回シードの構成も番号の `"7"` を通していた。
public class UndefinedMarketRejectionTests
{
    private static HttpClient Owner(MonitorWorkerWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    // ---- T-10-1720: 全置換は未定義の市場を 400 で拒み、何も保存しない（1 件の追加と揃える） ----

    [Theory]
    [InlineData(7)]
    [InlineData(-1)]
    public async Task T_10_1720_全置換は未定義の市場を400で拒み何も保存しない(int market)
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();
        using var client = Owner(factory);
        var before = await client.GetFromJsonAsync<List<MonitoredSymbol>>("/monitor/watchlist");

        var put = await client.PutAsJsonAsync("/monitor/settings", new
        {
            MovementThresholdRatio = 0.05m,
            Cooldown = TimeSpan.FromMinutes(10),
            MonitoredSymbols = new object[]
            {
                new { Symbol = "AAPL", Market = (int)Market.UnitedStates },
                new { Symbol = "MARS", Market = market },
            },
            Reason = "未定義の市場の置換",
        });

        await put.ShouldHaveStatusAsync(HttpStatusCode.BadRequest);
        (await put.Content.ReadAsStringAsync()).Should().Contain("未定義の市場").And.Contain($"MARS@{market}");
        (await client.GetFromJsonAsync<List<MonitoredSymbol>>("/monitor/watchlist"))
            .Should().BeEquivalentTo(before, "拒んだ置換は一部も保存しない");
    }

    // 対（1 件の追加は以前から同じ形で拒んでいた）: 入口の規則が 2 つの口で揃っていることを並べて固定する。
    [Fact]
    public async Task T_10_1720_一件の追加も未定義の市場を400で拒む()
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();
        using var client = Owner(factory);

        var post = await client.PostAsJsonAsync("/monitor/watchlist", new { Symbol = "MARS", Market = 7, Reason = "未定義の市場の追加" });

        await post.ShouldHaveStatusAsync(HttpStatusCode.BadRequest);
    }

    // 陽性対照: 定義済みの市場（日本・米国）の全置換は通る。
    [Fact]
    public async Task T_10_1720_定義済みの市場の全置換は通る()
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();
        using var client = Owner(factory);

        var put = await client.PutAsJsonAsync("/monitor/settings", new
        {
            MovementThresholdRatio = 0.05m,
            Cooldown = TimeSpan.FromMinutes(10),
            MonitoredSymbols = new[] { new MonitoredSymbol("7203", Market.Japan), new MonitoredSymbol("AAPL", Market.UnitedStates) },
            Reason = "定義済みの市場の置換",
        });

        await put.ShouldHaveStatusAsync(HttpStatusCode.OK);
    }

    // ---- T-10-1721: 初回シードの構成の未定義の市場は起動時に止める ----

    private sealed class SeededFactory(string market) : IAsyncDisposable
    {
        private readonly MonitorWorkerWebApplicationFactory _inner = new();

        internal Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Build() =>
            _inner.WithWebHostBuilder(b =>
            {
                b.UseSetting("Monitor:SeedSymbols:0:Symbol", "AAPL");
                b.UseSetting("Monitor:SeedSymbols:0:Market", market);
            });

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    [Theory]
    [InlineData("7")]
    [InlineData("-1")]
    public async Task T_10_1721_初回シードの構成の未定義の市場は起動時に止める(string market)
    {
        await using var seeded = new SeededFactory(market);
        using var factory = seeded.Build();

        var act = () => factory.CreateClient();

        act.Should().Throw<OptionsValidationException>().WithMessage("*Monitor:SeedSymbols*未定義の市場*");
    }

    // 陽性対照: 列挙名（UnitedStates）と定義済みの番号（1）は起動し、シードに使われる。
    [Theory]
    [InlineData("UnitedStates")]
    [InlineData("1")]
    public async Task T_10_1721_定義済みの市場の初回シードは起動しシードに使われる(string market)
    {
        await using var seeded = new SeededFactory(market);
        using var factory = seeded.Build();
        _ = factory.CreateClient();

        factory.Services.GetRequiredService<MonitorSeedOptions>().ToMonitoredSymbols()
            .Should().Equal(new MonitoredSymbol("AAPL", Market.UnitedStates));
    }
}
