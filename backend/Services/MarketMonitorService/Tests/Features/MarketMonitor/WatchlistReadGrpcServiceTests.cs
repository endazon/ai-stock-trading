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

    private static GrpcChannel ChannelFor(WebApplicationFactory<Program> factory, string? roles, string? azp = null)
    {
        var handler = factory.Server.CreateHandler();
        if (roles is not null)
            handler = new RolesHeaderHandler(handler, roles, azp);
        return GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = handler });
    }

    private sealed class RolesHeaderHandler(HttpMessageHandler inner, string roles, string? azp = null) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation(TestAuthHandler.RolesHeader, roles);
            if (azp is not null)
                request.Headers.TryAddWithoutValidation(TestAuthHandler.AzpHeader, azp);
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

    // #1067: gRPC 面の所有者の分岐は、トークンの azp がボットの機密クライアントであるときだけ（T-10-1725）。
    [Fact]
    public async Task T_10_1691_利用者のロールでも読める()
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();
        Arrange(factory, DateTimeOffset.UtcNow.AddDays(-3));
        using var channel = ChannelFor(factory, "trading-owner", "ai-stock-trading-owner");

        (await Grpc(channel).GetWatchlistAsync(new Proto.GetWatchlistRequest())).Items.Should().NotBeEmpty();
    }

    // ---- T-10-1725: gRPC 面の所有者の門は、呼び出し元のクライアント（azp）が Discord ボットであることを併せて求める ----
    // NFR-06, FR-14, ADR-0047 決定 3, IADR-0448 決定 1, #1067 (#753): 人の利用者のトークン（`trading-owner` を持つが azp は BFF 等）は
    // gRPC 面を通らない。🔴 変種（大小文字・接頭辞・接尾辞）と azp の無いトークンも通さない。s2s の分岐と REST の面は変えない。
    private const string GateOwnerRole = "trading-owner";
    private const string GateServiceRole = "trading-service";
    private const string BotClient = "ai-stock-trading-owner";

    [Theory]
    [InlineData(null)]                          // azp の無いトークン
    [InlineData("ai-stock-trading-dev")]        // 利用者の公開クライアント（ブラウザ・BFF の経路）
    [InlineData("bff")]
    [InlineData("AI-STOCK-TRADING-OWNER")]      // 大小文字の変種
    [InlineData("Ai-Stock-Trading-Owner")]
    [InlineData("ai-stock-trading-owner-bff")]  // ボットの id を接頭辞に持つ別のクライアント
    [InlineData("ai-stock-trading-own")]        // ボットの id の接頭辞
    [InlineData("xai-stock-trading-owner")]     // ボットの id を接尾辞に持つ別のクライアント
    [InlineData("ai-stock-trading-svc")]        // s2s のクライアントでも、所有者の分岐では通さない
    public async Task T_10_1725_所有者のロールでも呼び出し元がボットでなければ_PERMISSION_DENIED(string? azp)
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();
        using var channel = ChannelFor(factory, GateOwnerRole, azp);

        var act = async () => await Grpc(channel).GetWatchlistAsync(new Proto.GetWatchlistRequest());

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        var asOf = async () => await Grpc(channel).GetWatchlistAsOfAsync(
            new Proto.GetWatchlistAsOfRequest { At = Iso(DateTimeOffset.UtcNow) });
        (await asOf.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied, "同じ面の別の rpc も同じ門");
    }

    // 陽性対照: ボットのトークン（trading-owner ＋ azp＝ボットの機密クライアント）は通る。s2s は azp を問わず従来どおり。
    // 🔴 REST の面の所有者の判定は変えない（azp の無い利用者のトークンで REST は読める＝BFF が中継する経路）。
    [Fact]
    public async Task T_10_1725_ボットのトークンは通り_s2sは従来どおりで_RESTの所有者の判定は変えない()
    {
        await using var factory = new MonitorWorkerWebApplicationFactory();
        foreach (var (roles, azp) in new (string, string?)[]
                 {
                     (GateOwnerRole, BotClient),
                     (GateServiceRole, null),
                     (GateServiceRole, "bff"),
                     ($"{GateOwnerRole},{GateServiceRole}", "bff"),
                 })
        {
            using var channel = ChannelFor(factory, roles, azp);
            var act = async () => await Grpc(channel).GetWatchlistAsync(new Proto.GetWatchlistRequest());
            await act.Should().NotThrowAsync($"roles={roles} azp={azp ?? "(無し)"} は通るはず");
        }

        using var rest = Rest(factory, GateOwnerRole);
        (await rest.GetAsync("/monitor/watchlist")).StatusCode.Should().Be(HttpStatusCode.OK, "REST の所有者の判定は OwnerOrService のまま");
    }
}
