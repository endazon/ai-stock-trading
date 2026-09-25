using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.GetOpenPositions;
using RiskManagementService.Features.RiskManagement.GetSizingContext;
using RiskManagementService.Features.RiskManagement.GetWorkingEntryOrders;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RiskManagementService.Tests;

// 🔴 T-10-805, FR-04, FR-10, #943, IADR-0390: **本番の Program.cs が通信路に出す JSON は、読み取り口の応答型を web 既定
// （camelCase・列挙は数値）で直列化したものと一字一句同じである**ことを固定する。
//
// 他サービスの契約テスト（判断 T-10-744 / T-10-800 / T-10-802・市場監視 T-10-803・報告書 T-10-804）は、送り手の本物の型を
// **web 既定で直列化した本文**を受け手のアダプタに読ませている。その前提（＝送り手が JSON 設定を変えていない）は受け手の側からは
// 見えない —— 例えばリスク管理の Program.cs に `JsonStringEnumConverter` が足されると、受け手のテストは緑のまま実行時は列挙の
// 読み取りで例外（＝不明／空列）になる（費用統制・報告書の Program.cs は現に文字列列挙へ変えている）。本テストが送り手の側で
// それを赤にし、受け手側の契約テストと合わせて端から端までをつなぐ。
//
// 観測: 本物の Program.cs（RiskWorkerWebApplicationFactory。InMemory DB・TestAuthHandler だけを差し替え）へ trading-service ロールで
// 要求した応答本文と、同じ DI から引いたサービスの Build() を web 既定で直列化したものを JSON の木として突き合わせる。
public class ReadContractWireFormatTests
{
    private const string Service = "trading-service";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task 判断と市場監視と報告書が読む口の本文は応答型を_web_既定で直列化したものと同じ()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        Seed(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, Service);

        var openPositions = await BodyAsync(client, "/risk-controls/open-positions");
        var sizingContext = await BodyAsync(client, "/risk-controls/sizing-context");
        var workingEntries = await BodyAsync(client, "/risk-controls/working-entry-orders");

        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        IReadOnlyList<OpenPositionView> positions = sp.GetRequiredService<OpenPositionsService>().Build();
        SizingContextView context = sp.GetRequiredService<SizingContextService>().Build();
        IReadOnlyList<WorkingEntryOrderView> working = sp.GetRequiredService<WorkingEntryOrdersService>().Build();

        // 空の配列どうしの一致は何も証明しない。建玉 1 件・未約定 1 件が載っていることを先に確かめる。
        positions.Should().ContainSingle(p => p.Symbol == "AAPL" && p.Market == Market.UnitedStates);
        working.Should().ContainSingle(w => w.Symbol == "MSFT" && w.Market == Market.UnitedStates);

        JsonNode.DeepEquals(openPositions, JsonSerializer.SerializeToNode(positions, Web))
            .Should().BeTrue($"open-positions の本文が web 既定と異なる: {openPositions?.ToJsonString()}");
        JsonNode.DeepEquals(sizingContext, JsonSerializer.SerializeToNode(context, Web))
            .Should().BeTrue($"sizing-context の本文が web 既定と異なる: {sizingContext?.ToJsonString()}");
        JsonNode.DeepEquals(workingEntries, JsonSerializer.SerializeToNode(working, Web))
            .Should().BeTrue($"working-entry-orders の本文が web 既定と異なる: {workingEntries?.ToJsonString()}");

        // 受け手が依存している形を名指しで表明する（列挙は数値・camelCase）。
        openPositions!.AsArray().Single()!["market"]!.GetValue<int>().Should().Be((int)Market.UnitedStates);
        openPositions.AsArray().Single()!["symbol"]!.GetValue<string>().Should().Be("AAPL");
        sizingContext!["mode"]!.GetValueKind().Should().Be(JsonValueKind.Number);
    }

    // 🔴 T-10-885, FR-06, FR-10, FR-21, #957, IADR-0408: 強制買戻しの推定（GET /risk-controls/buy-in-inferences）の外側は
    // **匿名型**であり、受け手の契約テスト（報告書 T-10-884）は外側の項目名を送り手の型から得られない。その名前をここで固定する。
    // 外側の `inferences` が改名されると、受け手は `?? []` で「強制買戻し 0 件」と書く（fail-open の表示）。
    [Fact]
    public async Task 強制買戻しの推定の本文は外側の項目名と行の型を_web_既定で出す()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        var record = new BuyInInferenceRecord(
            Guid.NewGuid(), "TSLA", Market.UnitedStates, 10, 4, 1, 5, 5, day.AddDays(30), day,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        using (var scope = factory.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<IBuyInInferenceStore>().Append(record);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, Service);

        var body = (await BodyAsync(client, $"/risk-controls/buy-in-inferences?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}"))!.AsObject();

        body.Select(p => p.Key).Should().BeEquivalentTo(["periodCovered", "observedTradingDays", "inferences"]);
        body["periodCovered"]!.GetValueKind().Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
        JsonNode.DeepEquals(body["inferences"], JsonSerializer.SerializeToNode(new[] { record }, Web))
            .Should().BeTrue($"inferences の本文が web 既定と異なる: {body["inferences"]?.ToJsonString()}");
    }

    private static async Task<JsonNode?> BodyAsync(HttpClient client, string path)
    {
        var res = await client.GetAsync(path);
        res.StatusCode.Should().Be(HttpStatusCode.OK, path);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync());
    }

    // 台帳へ約定済みの建玉（AAPL・前日）と、当日の未約定の新規建て（MSFT・承認のみ）を積む。
    private static void Seed(RiskWorkerWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IPortfolioLedgerStore>();

        var filled = Guid.NewGuid();
        var yesterday = DateTimeOffset.UtcNow.AddDays(-1);
        ledger.AppendApproval(
            filled,
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
                10, 200m, PositionEffect.Open, StopLossPrice: 190m),
            yesterday);
        ledger.AppendFill(filled, $"open-{filled:N}", 10, 200m, yesterday);

        ledger.AppendApproval(
            Guid.NewGuid(),
            new OrderIntent("MSFT", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
                3, 400m, PositionEffect.Open, StopLossPrice: 380m),
            DateTimeOffset.UtcNow);
    }
}
