using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using MarketMonitorService.Domain;
using MarketMonitorService.Features.MarketMonitor;
using MarketMonitorService.Features.MarketMonitor.GetWatchlistAsOf;
using MarketMonitorService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;

namespace MarketMonitorService.Tests;

// T-10-1691（提供側）, NFR, FR-02, FR-04, FR-13, FR-15, MSP:ADR-0029, IADR-0284 決定 5（段 4）, IADR-0446 決定 2・3, #1061 (#753):
// 監視銘柄の読み取りの **gRPC 面**。REST（`GET /monitor/watchlist`・`GET /monitor/watchlist/as-of`）と**同じ値・同じ認可・同じ入力の検証**で
// あることを、本物の Program.cs（MonitorWorkerWebApplicationFactory）で固定する。REST の本文を送り手の型へ戻して提供側の写しで proto に
// したものと、gRPC の応答を message ごと等価比較する。🔴 本物の Program.cs で呼べること自体が `MapGrpcService` の登録の証拠である。
public class WatchlistReadGrpcServiceTests
{
    private const string ServiceRole = "trading-service";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static GrpcChannel ChannelFor(WebApplicationFactory<Program> factory, string? roles)
    {
        var handler = factory.Server.CreateHandler();
        if (roles is not null)
            handler = new RolesHeaderHandler(handler, roles);
        return GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = handler });
    }

    private sealed class RolesHeaderHandler(HttpMessageHandler inner, string roles) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation(TestAuthHandler.RolesHeader, roles);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static HttpClient Rest(MonitorWorkerWebApplicationFactory factory, string roles = ServiceRole)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    private static Proto.WatchlistRead.WatchlistReadClient Grpc(GrpcChannel channel) => new(channel);

    private static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");

    // 行を確実に作ってから SeededAt を与え、本物の書き手で日本と米国の監視銘柄を 1 件ずつ足す。
    private static DateTimeOffset Arrange(MonitorWorkerWebApplicationFactory factory, DateTimeOffset seededAt)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMonitoredSymbolStore>().GetSettings();
        var db = scope.ServiceProvider.GetRequiredService<MarketMonitorDbContext>();
        var row = db.MonitorSettings.Single(r => r.Id == SingletonKeys.Id);
        row.SeededAt = seededAt;
        db.SaveChanges();

        var svc = scope.ServiceProvider.GetRequiredService<MonitorWatchlistService>();
        svc.Add("META", Market.UnitedStates, "test-owner", "gRPC 面の試験");
        svc.Add("7203", Market.Japan, "test-owner", "gRPC 面の試験");
        return DateTimeOffset.UtcNow;
    }

    [Fact]
    public async Task T_10_1691_現在の監視銘柄は_REST_と同じ値を返し日本は未指定に化けない()
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();
        Arrange(factory, DateTimeOffset.UtcNow.AddDays(-3));
        using var rest = Rest(factory);
        using var channel = ChannelFor(factory, ServiceRole);

        var restRows = await rest.GetFromJsonAsync<List<MonitoredSymbol>>("/monitor/watchlist", Web);
        var grpc = await Grpc(channel).GetWatchlistAsync(new Proto.GetWatchlistRequest());

        restRows.Should().Contain(s => s.Symbol == "7203" && s.Market == Market.Japan, "空どうしの一致は何も証明しない");
        grpc.Items.Should().Equal(restRows!.Select(WatchlistWireMapping.ToProto));
        grpc.Items.Single(i => i.Symbol == "7203").Market.Should().Be(Proto.Market.Japan, "C# の 0＝日本は線上で 1");
    }

    [Theory]
    [InlineData("再構成できる時点")]
    [InlineData("seed より前の時点")]
    public async Task T_10_1691_当時の監視銘柄は_REST_と同じ値を返す(string how)
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();
        var seeded = DateTimeOffset.UtcNow.AddDays(-3);
        var afterAdd = Arrange(factory, seeded);
        var at = Iso(how == "再構成できる時点" ? afterAdd : seeded.AddDays(-1));
        using var rest = Rest(factory);
        using var channel = ChannelFor(factory, ServiceRole);

        var restBody = await rest.GetFromJsonAsync<WatchlistAsOfResponse>(
            $"/monitor/watchlist/as-of?at={Uri.EscapeDataString(at)}", Web);
        var grpc = await Grpc(channel).GetWatchlistAsOfAsync(new Proto.GetWatchlistAsOfRequest { At = at });

        grpc.Should().Be(WatchlistWireMapping.ToProto(restBody!));
        if (how == "再構成できる時点")
        {
            restBody!.Symbols.Should().HaveCount(2, "空どうしの一致は何も証明しない");
            grpc.Symbols.Items.Should().HaveCount(2);
        }
        else
        {
            grpc.HasReconstructed.Should().BeTrue("false も値であり、欠落と区別して運ぶ");
            grpc.Reconstructed.Should().BeFalse();
            grpc.Symbols.Should().BeNull("一覧が無いことを空の一覧と区別して運ぶ");
            grpc.Reason.Should().NotBeNullOrWhiteSpace();
        }
    }

    // REST の 400（時刻の欠落・オフセットの欠落・読めない）は INVALID_ARGUMENT。
    [Theory]
    [InlineData("")]
    [InlineData("2026-09-10T00:00:00")]
    [InlineData("not-a-time")]
    public async Task T_10_1691_当時の時刻の誤りは_REST_と同じく_INVALID_ARGUMENT(string at)
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();
        using var rest = Rest(factory);
        using var channel = ChannelFor(factory, ServiceRole);

        (await rest.GetAsync($"/monitor/watchlist/as-of?at={Uri.EscapeDataString(at)}")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        var act = async () => await Grpc(channel).GetWatchlistAsOfAsync(new Proto.GetWatchlistAsOfRequest { At = at });
        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task T_10_1691_資格情報が無ければ_UNAUTHENTICATED_ロール不足は_PERMISSION_DENIED()
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();
        using var anonymous = ChannelFor(factory, roles: null);
        using var unrelated = ChannelFor(factory, "some-unrelated-role");

        var anon = async () => await Grpc(anonymous).GetWatchlistAsync(new Proto.GetWatchlistRequest());
        var anonAsOf = async () => await Grpc(anonymous).GetWatchlistAsOfAsync(
            new Proto.GetWatchlistAsOfRequest { At = Iso(DateTimeOffset.UtcNow) });
        var denied = async () => await Grpc(unrelated).GetWatchlistAsync(new Proto.GetWatchlistRequest());

        (await anon.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
        (await anonAsOf.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
        (await denied.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        (await factory.CreateClient().GetAsync("/monitor/watchlist")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "REST も同じ");
        using var restDenied = Rest(factory, "some-unrelated-role");
        (await restDenied.GetAsync("/monitor/watchlist")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "REST も同じ");
    }

    [Fact]
    public async Task T_10_1691_利用者のロールでも読める()
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();
        Arrange(factory, DateTimeOffset.UtcNow.AddDays(-3));
        using var channel = ChannelFor(factory, "trading-owner");

        (await Grpc(channel).GetWatchlistAsync(new Proto.GetWatchlistRequest())).Items.Should().NotBeEmpty();
    }
}
