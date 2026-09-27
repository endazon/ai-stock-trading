extern alias MarketMonitorWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using MarketMonitorWorker::MarketMonitorService.Domain;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TradeDecisionService.Tests;

// 🔴 T-10-930, FR-02, FR-13, UC-06, #957, IADR-0095, IADR-0408（2026-09-25 追記。T-10-800 の同型）: 判断が読む市場監視の監視銘柄
// （GET /monitor/watchlist）に、**送り手の本物の型 `MonitoredSymbol` を送り手の実際の JSON 設定（web 既定＝camelCase・列挙は数値）で
// 直列化した応答**を読ませる。既存の `HttpWatchlistProviderTests` は受け手自身の型（`WatchedSymbol`）を直列化しており、送り手で
// `Symbol` を改名しても緑のまま、実行時は全行が「銘柄が空」で落ちて**空の watchlist を返す**（フォールバックではない）＝判断が 1 本も
// 走らない。受け手の実装は変えない。送り手がその設定で出していることは市場監視側の T-10-931 が本物の Program.cs で固定する。
public class MarketMonitorReadContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task 監視銘柄は送り手の本物の型を直列化した応答から読める()
    {
        IReadOnlyCollection<MonitoredSymbol> watchlist =
            [new MonitoredSymbol("7203", Market.Japan), new MonitoredSymbol("AAPL", Market.UnitedStates)];
        var fallback = new RecordingFallback();
        var provider = new HttpWatchlistProvider(
            new HttpClient(new StubHandler(JsonSerializer.Serialize(watchlist, Web))) { BaseAddress = new Uri("http://monitor") },
            fallback,
            NullLogger<HttpWatchlistProvider>.Instance);

        var read = await provider.GetWatchlistAsync();

        read.Should().Equal(new WatchedSymbol("7203", Market.Japan), new WatchedSymbol("AAPL", Market.UnitedStates));
        fallback.Called.Should().BeFalse("供給できた応答は既定 watchlist へ倒さない");
    }

    // 🔴 T-10-1549, FR-04, #1034, IADR-0440 決定 2, IADR-0420: 判断のプロンプト用の口（GetAuthoritativeWatchlistAsync）も同じ応答を
    // 送り手の本物の型で読む。送り手で項目名が変わると全行が「銘柄が空」で落ち、プロンプトに**空の一覧（0 件・対象外）**が載る
    // ＝不明を 0 件と書く事故になる。
    [Fact]
    public async Task 判断のプロンプト用の監視銘柄は送り手の本物の型を直列化した応答から読める()
    {
        IReadOnlyCollection<MonitoredSymbol> watchlist =
            [new MonitoredSymbol("AAPL", Market.UnitedStates), new MonitoredSymbol("META", Market.UnitedStates)];
        var fallback = new RecordingFallback();
        var provider = new HttpWatchlistProvider(
            new HttpClient(new StubHandler(JsonSerializer.Serialize(watchlist, Web))) { BaseAddress = new Uri("http://monitor") },
            fallback,
            NullLogger<HttpWatchlistProvider>.Instance);

        var read = await provider.GetAuthoritativeWatchlistAsync();

        read.Should().Equal(new WatchedSymbol("AAPL", Market.UnitedStates), new WatchedSymbol("META", Market.UnitedStates));
        fallback.Called.Should().BeFalse("プロンプト用の口は既定 watchlist を使わない");
    }

    private sealed class RecordingFallback : IWatchlistProvider
    {
        public bool Called { get; private set; }

        public Task<IReadOnlyList<WatchedSymbol>> GetWatchlistAsync(CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult<IReadOnlyList<WatchedSymbol>>([new WatchedSymbol("FALLBACK", Market.Japan)]);
        }

        public Task<IReadOnlyList<WatchedSymbol>?> GetAuthoritativeWatchlistAsync(CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult<IReadOnlyList<WatchedSymbol>?>(null);
        }
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
