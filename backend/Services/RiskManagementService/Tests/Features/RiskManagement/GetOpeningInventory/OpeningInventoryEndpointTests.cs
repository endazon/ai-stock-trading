using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.GetOpeningInventory;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace RiskManagementService.Tests;

// T-06-049, FR-06, FR-16, #1181, IADR-0493 決定 2: 期間開始時点の在庫の REST（`GET /risk-controls/opening-inventory`）と
// gRPC（`RiskControlsRead/GetOpeningInventory`）を本物の Program.cs（InMemory DB・TestAuthHandler）で固定する。
// サービストークンで読める（OwnerOrService）・必須引数の欠落は 400 / INVALID_ARGUMENT・gRPC は REST と同じ行を返す。
public class OpeningInventoryEndpointTests
{
    private const string Service = "trading-service";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class RolesHeaderHandler(HttpMessageHandler inner, string roles) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation(TestAuthHandler.RolesHeader, roles);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static HttpClient Rest(RiskWorkerWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, Service);
        return client;
    }

    private static Proto.RiskControlsRead.RiskControlsReadClient Grpc(RiskWorkerWebApplicationFactory factory, out GrpcChannel channel)
    {
        channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = new RolesHeaderHandler(factory.Server.CreateHandler(), Service),
        });
        return new Proto.RiskControlsRead.RiskControlsReadClient(channel);
    }

    // MSFT を 2 回建て（ET 10-01・10-02）、1 回目の一部を決済（ET 10-02）。ET 10-05 の買いは before=10-05 なら入らない。
    private static void Seed(RiskWorkerWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IPortfolioLedgerStore>();

        void Trade(TradeSide side, PositionEffect effect, int quantity, decimal price, DateTimeOffset at, decimal? rate)
        {
            var id = Guid.NewGuid();
            ledger.AppendApproval(
                id,
                new OrderIntent("MSFT", Market.UnitedStates, side, ProductType.Cash, BrokerProvider.MoomooSimulate,
                    quantity, price, effect),
                at.AddMinutes(-1),
                fxRateBaseToDisplay: rate);
            ledger.AppendFill(id, $"o-{id:N}", quantity, price, at, BrokerProvider.MoomooSimulate);
        }

        Trade(TradeSide.Buy, PositionEffect.Open, 300, 500m, new DateTimeOffset(2026, 10, 1, 14, 0, 0, TimeSpan.Zero), 150m);
        Trade(TradeSide.Buy, PositionEffect.Open, 200, 530m, new DateTimeOffset(2026, 10, 2, 14, 0, 0, TimeSpan.Zero), 148m);
        Trade(TradeSide.Sell, PositionEffect.Close, 32, 520m, new DateTimeOffset(2026, 10, 2, 15, 0, 0, TimeSpan.Zero), 149m);
        Trade(TradeSide.Buy, PositionEffect.Open, 10, 540m, new DateTimeOffset(2026, 10, 5, 14, 0, 0, TimeSpan.Zero), 147m);
    }

    [Fact]
    public async Task T06_049_サービストークンで読め_gRPC_は_REST_と同じ行を返す()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        Seed(factory);
        using var rest = Rest(factory);
        var grpc = Grpc(factory, out var channel);
        using var _ = channel;

        var restLots = await rest.GetFromJsonAsync<List<OpeningInventoryView>>(
            "/risk-controls/opening-inventory?market=UnitedStates&before=2026-10-05", Web);
        var grpcLots = await grpc.GetOpeningInventoryAsync(new Proto.GetOpeningInventoryRequest
        {
            Market = Proto.Market.UnitedStates,
            Before = "2026-10-05",
        });

        // (300×500 + 200×530) / 500 = 512。一部決済（32 株）は平均を変えない。ET 10-05 の買いは入らない。
        var lot = restLots.Should().ContainSingle().Subject;
        lot.Should().Be(new OpeningInventoryView(
            "MSFT", Market.UnitedStates, TradeSide.Buy, 468, 512m, (150000m * 150m + 106000m * 148m) / 256000m, 0));
        grpcLots.Lots.Should().Equal(restLots!.Select(RiskReadWireMapping.ToProto));
        grpcLots.Lots.Single().Market.Should().Be(Proto.Market.UnitedStates, "列挙は名前で写す（未指定の 0 ではない）");
    }

    [Fact]
    public async Task T06_049_市場を数値で渡しても読める_別の市場の在庫は空()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        Seed(factory);
        using var rest = Rest(factory);

        var byNumber = await rest.GetFromJsonAsync<List<OpeningInventoryView>>(
            $"/risk-controls/opening-inventory?market={(int)Market.UnitedStates}&before=2026-10-05", Web);
        var japan = await rest.GetFromJsonAsync<List<OpeningInventoryView>>(
            "/risk-controls/opening-inventory?market=Japan&before=2026-10-05", Web);

        byNumber.Should().ContainSingle();
        japan.Should().BeEmpty("空は「その時点で建玉なし」（照会は成功している）");
    }

    [Theory]
    [InlineData("/risk-controls/opening-inventory")]
    [InlineData("/risk-controls/opening-inventory?market=UnitedStates")]
    [InlineData("/risk-controls/opening-inventory?before=2026-10-05")]
    [InlineData("/risk-controls/opening-inventory?market=99&before=2026-10-05")]
    public async Task T06_049_市場と境界の欠落_未定義の市場は400(string path)
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        using var rest = Rest(factory);

        (await rest.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(Proto.Market.Unspecified, "2026-10-05")]
    [InlineData(Proto.Market.UnitedStates, "")]
    [InlineData(Proto.Market.UnitedStates, "2026/10/05")]
    public async Task T06_049_gRPC_は欠落を_INVALID_ARGUMENT_にする(Proto.Market market, string before)
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        var grpc = Grpc(factory, out var channel);
        using var _ = channel;

        var act = async () => await grpc.GetOpeningInventoryAsync(
            new Proto.GetOpeningInventoryRequest { Market = market, Before = before });

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }
}
