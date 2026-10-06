using System.Net;
using System.Text;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Infrastructure.ExternalServices;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace ReportService.Tests;

// T-06-053, FR-06, FR-16, NFR, #1181, IADR-0493 決定 2・4, IADR-0427 決定 3・5: 期間開始時点の在庫の受け手（REST・gRPC）の写しと倒す向き、
// および本番の Program.cs の結線（`RiskManagement:Grpc` → gRPC・`BaseUrl` → REST・どちらも無し → 常に未供給）。
public class OpeningInventorySourceTests(ReportWorkerWebApplicationFactory factory)
    : IClassFixture<ReportWorkerWebApplicationFactory>
{
    private static readonly DateOnly Before = new(2026, 10, 5);

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (HttpOpeningInventorySource Source, StubHandler Handler) Http(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(status, body);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://risk") };
        return (new HttpOpeningInventorySource(client, NullLogger<HttpOpeningInventorySource>.Instance), handler);
    }

    // ---- REST ----

    [Fact]
    public async Task T06_053_REST_は市場と境界で引き_向きを符号付き数量へ写す()
    {
        // 送り手の OpeningInventoryView と同形（camelCase・列挙は数値）。ロング MSFT・ショート TSLA。
        var (source, handler) = Http(HttpStatusCode.OK, """
            [
              {"symbol":"MSFT","market":1,"side":0,"quantity":468,"averageCostInBase":511.912,"averageFxRateBaseToDisplay":150.25,"unrecordedFxRateFillCount":0},
              {"symbol":"TSLA","market":1,"side":1,"quantity":30,"averageCostInBase":250,"averageFxRateBaseToDisplay":null,"unrecordedFxRateFillCount":2}
            ]
            """);

        var lots = await source.GetOpeningInventoryAsync(Market.UnitedStates, Before);

        handler.Requested.Should().Equal(["/risk-controls/opening-inventory?market=UnitedStates&before=2026-10-05"]);
        lots.Should().Equal(
            new OpeningLot("MSFT", Market.UnitedStates, 468, 511.912m, 150.25m, 0),
            new OpeningLot("TSLA", Market.UnitedStates, -30, 250m, null, 2));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "[]")] // 旧版のリスク管理（口が無い）
    [InlineData(HttpStatusCode.InternalServerError, "[]")]
    [InlineData(HttpStatusCode.OK, "null")]
    [InlineData(HttpStatusCode.OK, "{not json")]
    // 必須の項目の欠落・市場の食い違い・数量 0 は**応答全体を読めない**（既定値で在庫を作らない）。
    [InlineData(HttpStatusCode.OK, """[{"symbol":"MSFT","side":0,"quantity":1,"averageCostInBase":1,"averageFxRateBaseToDisplay":150,"unrecordedFxRateFillCount":0}]""")]
    [InlineData(HttpStatusCode.OK, """[{"symbol":"MSFT","market":1,"quantity":1,"averageCostInBase":1,"averageFxRateBaseToDisplay":150,"unrecordedFxRateFillCount":0}]""")]
    [InlineData(HttpStatusCode.OK, """[{"symbol":"MSFT","market":1,"side":0,"averageCostInBase":1,"averageFxRateBaseToDisplay":150,"unrecordedFxRateFillCount":0}]""")]
    [InlineData(HttpStatusCode.OK, """[{"symbol":"MSFT","market":1,"side":0,"quantity":1,"averageFxRateBaseToDisplay":150,"unrecordedFxRateFillCount":0}]""")]
    [InlineData(HttpStatusCode.OK, """[{"symbol":"MSFT","market":1,"side":0,"quantity":1,"averageCostInBase":1,"averageFxRateBaseToDisplay":150}]""")]
    [InlineData(HttpStatusCode.OK, """[{"market":1,"side":0,"quantity":1,"averageCostInBase":1,"averageFxRateBaseToDisplay":150,"unrecordedFxRateFillCount":0}]""")]
    [InlineData(HttpStatusCode.OK, """[{"symbol":"7203","market":0,"side":0,"quantity":1,"averageCostInBase":1,"averageFxRateBaseToDisplay":150,"unrecordedFxRateFillCount":0}]""")]
    [InlineData(HttpStatusCode.OK, """[{"symbol":"MSFT","market":1,"side":0,"quantity":0,"averageCostInBase":1,"averageFxRateBaseToDisplay":150,"unrecordedFxRateFillCount":0}]""")]
    // T-06-057（独立監査 🟢1）: 同じ銘柄の重複・負の平均取得単価・レートと未記録の数の食い違い・正でないレートも読めない。
    [InlineData(HttpStatusCode.OK, """[{"symbol":"MSFT","market":1,"side":0,"quantity":1,"averageCostInBase":1,"averageFxRateBaseToDisplay":150,"unrecordedFxRateFillCount":0},{"symbol":"MSFT","market":1,"side":0,"quantity":2,"averageCostInBase":1,"averageFxRateBaseToDisplay":150,"unrecordedFxRateFillCount":0}]""")]
    [InlineData(HttpStatusCode.OK, """[{"symbol":"MSFT","market":1,"side":0,"quantity":1,"averageCostInBase":-1,"averageFxRateBaseToDisplay":150,"unrecordedFxRateFillCount":0}]""")]
    [InlineData(HttpStatusCode.OK, """[{"symbol":"MSFT","market":1,"side":0,"quantity":1,"averageCostInBase":1,"averageFxRateBaseToDisplay":null,"unrecordedFxRateFillCount":0}]""")]
    [InlineData(HttpStatusCode.OK, """[{"symbol":"MSFT","market":1,"side":0,"quantity":1,"averageCostInBase":1,"averageFxRateBaseToDisplay":150,"unrecordedFxRateFillCount":1}]""")]
    [InlineData(HttpStatusCode.OK, """[{"symbol":"MSFT","market":1,"side":0,"quantity":1,"averageCostInBase":1,"averageFxRateBaseToDisplay":0,"unrecordedFxRateFillCount":0}]""")]
    public async Task T06_053_REST_の不達と読めない応答は未供給(HttpStatusCode status, string body)
    {
        var (source, _) = Http(status, body);

        (await source.GetOpeningInventoryAsync(Market.UnitedStates, Before)).Should().BeNull("空列（建玉なし）へ倒さない");
    }

    [Fact]
    public async Task T06_053_REST_の空列は建玉なし()
    {
        var (source, _) = Http(HttpStatusCode.OK, "[]");

        (await source.GetOpeningInventoryAsync(Market.Japan, Before)).Should().BeEmpty();
    }

    // ---- gRPC（本番の組み立て・偽の提供側） ----

    [Fact]
    public async Task T06_053_Grpc_を宣言すれば_gRPC_実装で引き_REST_と同じ解釈で写す()
    {
        var response = new Proto.GetOpeningInventoryResponse();
        response.Lots.Add(new Proto.OpeningInventoryLot
        {
            Symbol = "NVDA",
            Market = Proto.Market.UnitedStates,
            Side = Proto.TradeSide.Buy,
            Quantity = 1049,
            AverageCostInBase = "230.77",
            AverageFxRateBaseToDisplay = "149.5",
            UnrecordedFxRateFillCount = 0,
        });
        var behavior = new RiskReadStubBehavior { OpeningInventory = RiskReadStubBehavior.Returns(response) };
        await using var host = await RiskReadStubHost.StartAsync(behavior);
        using var configured = factory.WithWebHostBuilder(b => b.UseSetting("RiskManagement:Grpc", host.Address));

        var source = configured.Services.GetRequiredService<IOpeningInventorySource>();
        var lots = await source.GetOpeningInventoryAsync(Market.UnitedStates, Before);

        source.Should().BeOfType<GrpcOpeningInventorySource>();
        lots.Should().Equal(new OpeningLot("NVDA", Market.UnitedStates, 1049, 230.77m, 149.5m, 0));
        behavior.OpeningInventoryRequests.Should().Equal([(Proto.Market.UnitedStates, "2026-10-05")]);
    }

    [Fact]
    public async Task T06_053_gRPC_の旧版の提供側と欠けた行は未供給()
    {
        // 旧版のリスク管理（rpc が無い）＝ UNIMPLEMENTED。
        await using (var old = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior()))
        {
            using var configured = factory.WithWebHostBuilder(b => b.UseSetting("RiskManagement:Grpc", old.Address));
            (await configured.Services.GetRequiredService<IOpeningInventorySource>()
                .GetOpeningInventoryAsync(Market.UnitedStates, Before)).Should().BeNull();
        }

        // 平均取得単価が欠けた行（送り手の改名など）。
        var broken = new Proto.GetOpeningInventoryResponse();
        broken.Lots.Add(new Proto.OpeningInventoryLot
        {
            Symbol = "NVDA",
            Market = Proto.Market.UnitedStates,
            Side = Proto.TradeSide.Buy,
            Quantity = 1,
            UnrecordedFxRateFillCount = 0,
        });
        await using var host = await RiskReadStubHost.StartAsync(
            new RiskReadStubBehavior { OpeningInventory = RiskReadStubBehavior.Returns(broken) });
        using var withBroken = factory.WithWebHostBuilder(b => b.UseSetting("RiskManagement:Grpc", host.Address));
        (await withBroken.Services.GetRequiredService<IOpeningInventorySource>()
            .GetOpeningInventoryAsync(Market.UnitedStates, Before)).Should().BeNull("既定値（0）の取得原価で在庫を作らない");

        // T-06-057（独立監査 🟢1）: 同じ銘柄の重複行も gRPC で読めない（REST と同じ解釈）。
        var duplicated = new Proto.GetOpeningInventoryResponse();
        for (var i = 0; i < 2; i++)
        {
            duplicated.Lots.Add(new Proto.OpeningInventoryLot
            {
                Symbol = "NVDA",
                Market = Proto.Market.UnitedStates,
                Side = Proto.TradeSide.Buy,
                Quantity = 1 + i,
                AverageCostInBase = "230.77",
                AverageFxRateBaseToDisplay = "150",
                UnrecordedFxRateFillCount = 0,
            });
        }

        await using var dupHost = await RiskReadStubHost.StartAsync(
            new RiskReadStubBehavior { OpeningInventory = RiskReadStubBehavior.Returns(duplicated) });
        using var withDup = factory.WithWebHostBuilder(b => b.UseSetting("RiskManagement:Grpc", dupHost.Address));
        (await withDup.Services.GetRequiredService<IOpeningInventorySource>()
            .GetOpeningInventoryAsync(Market.UnitedStates, Before)).Should().BeNull("重複は後勝ちで黙って畳まない");

        // 失敗の status（UNAVAILABLE）も未供給。
        await using var failing = await RiskReadStubHost.StartAsync(
            new RiskReadStubBehavior { OpeningInventory = RiskReadStubBehavior.Fails<Proto.GetOpeningInventoryResponse>(StatusCode.Unavailable) });
        using var withFailing = factory.WithWebHostBuilder(b => b.UseSetting("RiskManagement:Grpc", failing.Address));
        (await withFailing.Services.GetRequiredService<IOpeningInventorySource>()
            .GetOpeningInventoryAsync(Market.UnitedStates, Before)).Should().BeNull();
    }

    // ---- 結線（REST・未構成） ----

    [Fact]
    public async Task T06_053_宣言が無ければ_REST_所在も無ければ常に未供給()
    {
        using var rest = factory.WithWebHostBuilder(b => b.UseSetting("RiskManagement:BaseUrl", "http://risk"));
        rest.Services.GetRequiredService<IOpeningInventorySource>().Should().BeOfType<HttpOpeningInventorySource>();

        var none = factory.Services.GetRequiredService<IOpeningInventorySource>();
        none.Should().BeOfType<UnsuppliedOpeningInventorySource>();
        (await none.GetOpeningInventoryAsync(Market.UnitedStates, Before)).Should().BeNull("空列（建玉なし）へ倒さない");
    }
}
