using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using MarketMonitorService.Features.MarketMonitor;
using MarketMonitorService.Features.MarketMonitor.ApplyWatchlistProposal;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;

namespace MarketMonitorService.Tests;

// T-10-1734（提供側）, NFR, NFR-06, FR-13, FR-14, MSP:ADR-0029, ADR-0047 決定 1〜3, IADR-0284 決定 5（段 5）, IADR-0450 決定 2・3, #753:
// Discord ボットの**入れ替え案の適用の gRPC 面**（`WatchlistOwnerWrite/ApplyWatchlistProposal`）。REST の `POST /monitor/watchlist/proposal-apply` と
// **同じ処理関数**を通ること（同じ内訳・同じ変更者の記録・同じ拒否の分類と文言）と、門が `GrpcOwnerOnly`（ボット可・s2s 不可・人の利用者不可）で
// あることを、本物の Program.cs（MonitorWorkerWebApplicationFactory）で固定する。🔴 呼べること自体が `MapGrpcService` の登録の証拠である。
// 観測の仕方: 同じ種から 2 つのホストを立て、片方は REST・片方は gRPC で同じ要求を送り、応答と状態（監視銘柄・変更履歴）を比べる。
public class WatchlistOwnerWriteGrpcServiceTests
{
    private const string OwnerRole = "trading-owner";
    private const string ServiceRole = "trading-service";
    private const string Bot = "ai-stock-trading-owner";
    private const string ProposalRef = "daily-2026-09-28-v3";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static WebApplicationFactory<Program> Configured(MonitorWorkerWebApplicationFactory baseFactory) =>
        baseFactory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Monitor:DelegatedActor:TrustedClientIds", Bot);
            b.UseSetting("MarketData:Provider", "finnhub");
            b.UseSetting("Monitor:PollIntervalSeconds", "60");
        });

    // ボットのトークン（名前クレーム無し・azp はボットの機密クライアント）。
    private sealed class Headers(HttpMessageHandler inner, string? roles, string? azp) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (roles is not null)
            {
                request.Headers.TryAddWithoutValidation(TestAuthHandler.RolesHeader, roles);
                request.Headers.TryAddWithoutValidation(TestAuthHandler.NameHeader, TestAuthHandler.NoName);
            }

            if (azp is not null)
                request.Headers.TryAddWithoutValidation(TestAuthHandler.AzpHeader, azp);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static Proto.WatchlistOwnerWrite.WatchlistOwnerWriteClient Grpc(WebApplicationFactory<Program> factory, string? roles = OwnerRole, string? azp = Bot) =>
        new(GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = new Headers(factory.Server.CreateHandler(), roles, azp) }));

    // REST もボットと同じトークン（名前クレーム無し・azp はボット）で呼ぶ。
    private static HttpClient Rest(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);
        client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, TestAuthHandler.NoName);
        client.DefaultRequestHeaders.Add(TestAuthHandler.AzpHeader, Bot);
        return client;
    }

    private static HttpClient Owner(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);
        return client;
    }

    // 既定の監視銘柄は空なので、比べる前に同じ種を入れる（空どうしの一致は何も証明しない）。
    private static async Task SeedAsync(WebApplicationFactory<Program> factory)
    {
        foreach (var symbol in new[] { "MSFT", "AAPL" })
            (await Owner(factory).PostAsJsonAsync("/monitor/watchlist", new { Symbol = symbol, Market = Market.UnitedStates, Reason = "初期" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<List<string>> SymbolsAsync(WebApplicationFactory<Program> factory) =>
        [.. (await Owner(factory).GetFromJsonAsync<JsonElement>("/monitor/watchlist")).EnumerateArray().Select(e => e.GetProperty("symbol").GetString()!)];

    private static object RestBody(IEnumerable<string> expected, (string Action, string Symbol, string Reason)[] changes, string proposalRef = ProposalRef, string? onBehalfOf = "developer") => new
    {
        ExpectedWatchlist = expected.Select(s => new { Symbol = s, Market = Market.UnitedStates }).ToArray(),
        Changes = changes.Select(c => new { c.Action, c.Symbol, c.Reason }).ToArray(),
        ProposalRef = proposalRef,
        OnBehalfOf = onBehalfOf,
    };

    private static Proto.WatchlistProposalApplicationRequest GrpcBody(
        IEnumerable<string> expected, (string Action, string Symbol, string Reason)[] changes, string proposalRef = ProposalRef,
        string? onBehalfOf = "developer", Proto.Market market = Proto.Market.UnitedStates)
    {
        var request = new Proto.WatchlistProposalApplicationRequest
        {
            ExpectedWatchlist = new Proto.WatchlistItems(),
            Changes = new Proto.WatchlistChangeInstructions(),
            ProposalRef = proposalRef,
        };
        if (onBehalfOf is not null)
            request.OnBehalfOf = onBehalfOf;
        request.ExpectedWatchlist.Items.AddRange(expected.Select(s => new Proto.WatchlistItem { Symbol = s, Market = market }));
        request.Changes.Items.AddRange(changes.Select(c => new Proto.WatchlistChangeInstruction { Action = c.Action, Symbol = c.Symbol, Reason = c.Reason }));
        return request;
    }

    [Fact]
    public async Task T_10_1734_入れ替え案の適用は_REST_と同じ内訳と変更者を返す()
    {
        await using var baseRest = new MonitorWorkerWebApplicationFactory();
        await using var restHost = Configured(baseRest);
        await using var baseGrpc = new MonitorWorkerWebApplicationFactory();
        await using var grpcHost = Configured(baseGrpc);
        await SeedAsync(restHost);
        await SeedAsync(grpcHost);
        var before = await SymbolsAsync(restHost);
        (await SymbolsAsync(grpcHost)).Should().Equal(before, "同じ種から始める");
        (string, string, string)[] changes = [("add", "NVDA", "AI 需要"), ("remove", before[0], "値動きが小さい"), ("remove", "ZZZZ", "無い")];

        using var restResponse = await Rest(restHost).PostAsJsonAsync("/monitor/watchlist/proposal-apply", RestBody(before, changes));
        var grpc = await Grpc(grpcHost).ApplyWatchlistProposalAsync(GrpcBody(before, changes));

        restResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var rest = await restResponse.Content.ReadFromJsonAsync<WatchlistProposalApplyResponse>(Web);
        rest!.Items.Should().HaveCount(3, "空どうしの一致は何も証明しない");
        grpc.Should().Be(WatchlistOwnerWriteGrpcService.ToProto(rest));
        grpc.Actor.Should().Be("developer", "変更者は本文の on_behalf_of（信頼クライアントのトークンに限る）");
        grpc.Items.Select(i => i.HasApplied && i.Applied).Should().Equal(true, true, false);
        (await SymbolsAsync(grpcHost)).Should().BeEquivalentTo(await SymbolsAsync(restHost), "同じ処理関数を通るので状態も同じ");
    }

    // REST の 409（案の作成後に変わった）→ ABORTED、400（案の形・出所・代理の値域外・市場の欠落）→ INVALID_ARGUMENT。文言は REST の error と同じ。
    [Fact]
    public async Task T_10_1734_拒否は_REST_と同じ分類と文言で1件も適用しない()
    {
        await using var baseFactory = new MonitorWorkerWebApplicationFactory();
        await using var factory = Configured(baseFactory);
        await SeedAsync(factory);
        var before = await SymbolsAsync(factory);
        (string, string, string)[] add = [("add", "NVDA", "r")];

        var cases = new (object RestBody, Proto.WatchlistProposalApplicationRequest GrpcBody, HttpStatusCode RestStatus, StatusCode GrpcStatus)[]
        {
            (RestBody(["ZZZZ"], add), GrpcBody(["ZZZZ"], add), HttpStatusCode.Conflict, StatusCode.Aborted),
            (RestBody(before, [("buy", "NVDA", "r")]), GrpcBody(before, [("buy", "NVDA", "r")]), HttpStatusCode.BadRequest, StatusCode.InvalidArgument),
            (RestBody(before, add, proposalRef: "bad ref"), GrpcBody(before, add, proposalRef: "bad ref"), HttpStatusCode.BadRequest, StatusCode.InvalidArgument),
            (RestBody(before, add, onBehalfOf: "bad name\n"), GrpcBody(before, add, onBehalfOf: "bad name\n"), HttpStatusCode.BadRequest, StatusCode.InvalidArgument),
            // 🔴 市場の未指定は REST の市場の省略（null）と同じ＝ 400（日本と読まない）。
            (new { ExpectedWatchlist = before.Select(s => new { Symbol = s }).ToArray(), Changes = new[] { new { Action = "add", Symbol = "NVDA", Reason = "r" } }, ProposalRef, OnBehalfOf = "developer" },
                GrpcBody(before, add, market: Proto.Market.Unspecified), HttpStatusCode.BadRequest, StatusCode.InvalidArgument),
        };

        foreach (var (restBody, grpcBody, restStatus, grpcStatus) in cases)
        {
            using var response = await Rest(factory).PostAsJsonAsync("/monitor/watchlist/proposal-apply", restBody);
            response.StatusCode.Should().Be(restStatus);
            var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

            var act = async () => await Grpc(factory).ApplyWatchlistProposalAsync(grpcBody);
            var ex = (await act.Should().ThrowAsync<RpcException>()).Which;
            ex.StatusCode.Should().Be(grpcStatus);
            ex.Status.Detail.Should().Be(error, "REST の error を状態の詳細に載せる（利用者へそのまま見せる文言）");
        }

        (await SymbolsAsync(factory)).Should().Equal(before, "拒否では 1 件も適用しない");
    }

    // T-10-2425（NFR-06, IADR-0509, #1230）: 案の形の検証（自前の入力検証）は印つき（ClientVisibleArgument）なので、REST の 400 と
    // gRPC の INVALID_ARGUMENT が固定文言ではなく利用者へ見せる文言を返す（T-10-1734 は両者の一致だけを見ており、両方が固定文言でも通る）。
    [Fact]
    public async Task T_10_2425_案の形の検証の文言は印つきで_REST_と_gRPC_に載る()
    {
        await using var baseFactory = new MonitorWorkerWebApplicationFactory();
        await using var factory = Configured(baseFactory);
        await SeedAsync(factory);
        var before = await SymbolsAsync(factory);
        // 米国のティッカーでない銘柄（小文字）。操作の語彙は端点で弾かれるため、ここでは案の形の検証（ApplyProposal）まで届く値を使う。
        (string, string, string)[] invalid = [("add", "nvda", "r")];

        using var response = await Rest(factory).PostAsJsonAsync("/monitor/watchlist/proposal-apply", RestBody(before, invalid));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        error.Should().StartWith("銘柄は米国のティッカー（大文字。例 AAPL / BRK.B）に限ります。").And.NotBe(ClientFacingErrors.InvalidRequestMessage);

        var act = async () => await Grpc(factory).ApplyWatchlistProposalAsync(GrpcBody(before, invalid));
        var ex = (await act.Should().ThrowAsync<RpcException>()).Which;
        (ex.StatusCode, ex.Status.Detail).Should().Be((StatusCode.InvalidArgument, error!));
    }

    // ---- 門（GrpcOwnerOnly） ----

    [Theory]
    [InlineData(OwnerRole, null)]                   // azp の無い所有者（人の利用者）
    [InlineData(OwnerRole, "ai-stock-trading-dev")]
    [InlineData(OwnerRole, "bff")]
    [InlineData(ServiceRole, Bot)]                  // 🔴 s2s には開かない（REST の OwnerOnly と同じ）
    [InlineData(ServiceRole, "ai-stock-trading-svc")]
    public async Task T_10_1734_適用の面はボット以外を_PERMISSION_DENIED(string roles, string? azp)
    {
        await using var baseFactory = new MonitorWorkerWebApplicationFactory();
        await using var factory = Configured(baseFactory);
        await SeedAsync(factory);
        var before = await SymbolsAsync(factory);

        var act = async () => await Grpc(factory, roles, azp).ApplyWatchlistProposalAsync(GrpcBody(before, [("add", "NVDA", "r")]));

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        (await SymbolsAsync(factory)).Should().Equal(before);
    }

    [Fact]
    public async Task T_10_1734_資格情報が無ければ_UNAUTHENTICATED()
    {
        await using var baseFactory = new MonitorWorkerWebApplicationFactory();
        await using var factory = Configured(baseFactory);

        var act = async () => await Grpc(factory, roles: null, azp: null).ApplyWatchlistProposalAsync(GrpcBody(["AAPL"], [("add", "NVDA", "r")]));

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }
}
