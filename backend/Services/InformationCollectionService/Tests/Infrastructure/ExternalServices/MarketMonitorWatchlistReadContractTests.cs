extern alias MarketMonitorWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Infrastructure.ExternalServices;
using MarketMonitorWorker::MarketMonitorService.Domain;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InformationCollectionService.Tests;

// 🔴 T-10-1468, FR-01, FR-13, #1015, IADR-0435, IADR-0420 決定1: 情報収集が読む市場監視の監視銘柄（GET /monitor/watchlist）に、
// **送り手の本物の型 `MonitoredSymbol` を送り手の実際の JSON 設定（web 既定＝camelCase・列挙は数値）で直列化した応答**を読ませる。
// 送り手で項目を改名すると、受け手は全行を「項目の欠けた行」として一覧ごと不明に倒す（直前の値・構成へ倒れ、監視銘柄の変更が
// 収集に届かなくなる）。本テストはその食い違いを同じビルドで赤にする。送り手がその設定で出していることは市場監視側の
// T-10-931（ReadContractWireFormatTests）が本物の Program.cs で固定する。
public class MarketMonitorWatchlistReadContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task 監視銘柄は送り手の本物の型を直列化した応答から読める()
    {
        IReadOnlyCollection<MonitoredSymbol> watchlist =
            [new MonitoredSymbol("7203", Market.Japan), new MonitoredSymbol("AAPL", Market.UnitedStates)];
        var reader = new HttpMarketMonitorWatchlistReader(
            new HttpClient(new StubHandler(JsonSerializer.Serialize(watchlist, Web))) { BaseAddress = new Uri("http://monitor") },
            NullLogger<HttpMarketMonitorWatchlistReader>.Instance);

        var read = await reader.ReadAsync();

        read.Should().Equal(new WatchedSymbol("7203", Market.Japan), new WatchedSymbol("AAPL", Market.UnitedStates));
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
