extern alias RiskManagementWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RiskManagementWorker::RiskManagementService.Features.RiskManagement.GetOpenPositions;
using Xunit;

namespace InformationCollectionService.Tests;

// 🔴 FR-01, FR-02, #1363, IADR-0547 決定 1: 収集の対象へ足す保有銘柄を、リスク管理の GET /risk-controls/open-positions から読む。
// 行の検証は取引判断の HttpHeldPositionProvider.InterpretHeldSymbols（IADR-0521 決定 1）と同じ規則 —— 1 行でも欠けていれば一覧ごと不明。
public class HttpRiskManagementHeldSymbolsProviderTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // 契約: 送り手の本物の型（OpenPositionView）を送り手の JSON 設定（web 既定）で直列化した応答から読める。
    // 送り手で項目を改名すると、全行が「項目の欠けた行」となり一覧ごと不明へ倒れる（ここが赤になる）。
    [Fact]
    public async Task 送り手の本物の型を直列化した応答から保有銘柄を読める()
    {
        IReadOnlyList<OpenPositionView> positions =
        [
            new("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 180m, 170m),
            new("7203", Market.Japan, TradeSide.Buy, 100, 2500m, 2400m),
            new("GOOGL", Market.UnitedStates, TradeSide.Sell, 5, 150m, 160m),
        ];
        var handler = new RecordingHandler(HttpStatusCode.OK, JsonSerializer.Serialize(positions, Web));

        var held = await Create(handler).GetHeldSymbolsAsync();

        held.Should().Equal(
            new WatchedSymbol("AAPL", Market.UnitedStates),
            new WatchedSymbol("7203", Market.Japan),
            new WatchedSymbol("GOOGL", Market.UnitedStates));
        handler.Path.Should().Be("/risk-controls/open-positions");
    }

    // 空の一覧は「保有なし」（事実としての空）。
    [Fact]
    public async Task 空の一覧は保有なし()
    {
        var held = await Create(new RecordingHandler(HttpStatusCode.OK, "[]")).GetHeldSymbolsAsync();

        held.Should().NotBeNull().And.BeEmpty();
    }

    // (銘柄, 市場) ごとに符号付きで合計し、0 になったものは保有に数えない。
    [Fact]
    public async Task 符号付きの合計が0の銘柄は保有に数えない()
    {
        const string body = """
            [{"symbol":"AAPL","market":1,"side":0,"quantity":10},
             {"symbol":"AAPL","market":1,"side":1,"quantity":10},
             {"symbol":"MSFT","market":1,"side":0,"quantity":3}]
            """;

        var held = await Create(new RecordingHandler(HttpStatusCode.OK, body)).GetHeldSymbolsAsync();

        held.Should().Equal(new WatchedSymbol("MSFT", Market.UnitedStates));
    }

    // 🔴 原則 A: 欠けた行・数量が正でない行が 1 つでもあれば一覧ごと不明（黙ってその銘柄を落とさない）。
    [Theory]
    [InlineData("""[{"symbol":"AAPL","market":1,"side":0,"quantity":10},{"market":1,"side":0,"quantity":3}]""")]
    [InlineData("""[{"symbol":"AAPL","market":1,"side":0,"quantity":10},{"symbol":"MSFT","side":0,"quantity":3}]""")]
    [InlineData("""[{"symbol":"AAPL","market":1,"side":0,"quantity":10},{"symbol":"MSFT","market":1,"quantity":3}]""")]
    [InlineData("""[{"symbol":"AAPL","market":1,"side":0,"quantity":10},{"symbol":"MSFT","market":1,"side":0,"quantity":0}]""")]
    [InlineData("""[{"symbol":"AAPL","market":1,"side":0,"quantity":10},{"symbol":"MSFT","market":7,"side":0,"quantity":3}]""")]
    [InlineData("""[{"symbol":"AAPL","market":1,"side":0,"quantity":10},null]""")]
    public async Task 欠けた行が1つでもあれば一覧ごと不明(string body)
    {
        var held = await Create(new RecordingHandler(HttpStatusCode.OK, body)).GetHeldSymbolsAsync();

        held.Should().BeNull();
    }

    // 🔴 原則 A: 非 2xx・読めない本文は不明（空へ倒さない）。
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "[]")]
    [InlineData(HttpStatusCode.InternalServerError, "[]")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, "null")]
    public async Task 照会の失敗は不明(HttpStatusCode status, string body)
    {
        var held = await Create(new RecordingHandler(status, body)).GetHeldSymbolsAsync();

        held.Should().BeNull();
    }

    private static HttpRiskManagementHeldSymbolsProvider Create(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://risk") },
            NullLogger<HttpRiskManagementHeldSymbolsProvider>.Instance);

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Path { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
