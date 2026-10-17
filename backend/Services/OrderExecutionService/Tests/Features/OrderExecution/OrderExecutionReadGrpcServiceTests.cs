using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.QueryDailyBars;
using OrderExecutionService.Features.OrderExecution.QueryInFlightCloses;
using OrderExecutionService.Features.OrderExecution.QueryShortPermit;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.OrderExecution.V1;

namespace OrderExecutionService.Tests;

// T-10-2900〜2904（提供側）, NFR, FR-04, FR-10, FR-15, UC-06, MSP:ADR-0029, IADR-0284 決定 5（段 5 の追補 2）, IADR-0448, IADR-0559 決定 1・2, #1391 (#753):
// 発注執行の**読み取りの gRPC 面**（`OrderExecutionRead`）。REST（`GET /order-execution/*`）と**同じ値・同じ門の向き・同じ入力の検証**であることを、
// 本物の Program.cs（ExecutionWorkerWebApplicationFactory。InMemory DB・TestAuthHandler・外部輸送の無効化と、ブローカーの境界の答えだけを差し替え）で固定する。
//
// 観測の仕方: REST の本文を**送り手の本物の型**へ戻し、それを提供側の写し（OrderExecutionReadWireMapping）で proto にしたものと、gRPC の応答を
// message ごと等価比較する。🔴 **本物の Program.cs で呼べること自体が「`MapGrpcService` が組み立てに入っている」証拠である**（登録を消すと UNIMPLEMENTED で赤）。
public class OrderExecutionReadGrpcServiceTests
{
    private const string ServiceRole = "trading-service";
    private const string OwnerRole = "trading-owner";
    private const string BotClient = "ai-stock-trading-owner";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly DailyBarView Bar1 = new(new DateOnly(2026, 9, 25), 250.5m, 252m, 249.25m, 251.75m, 41_234_567);
    private static readonly DailyBarView Bar2 = new(new DateOnly(2026, 9, 28), 251.75m, 255.5m, 250m, 254.125m, 52_345_678);

    private static WebApplicationFactory<Program> Wire(
        ExecutionWorkerWebApplicationFactory factory, IShortPermitSource? permits = null, IDailyKLineSource? bars = null) =>
        factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            // 内蔵 paper 構成はブローカーの照会ポートを登録しない。答え（ブローカーの境界）だけを足し、口・サービス・gRPC の面は本物のまま組む。
            services.RemoveAll<IShortPermitSource>();
            services.RemoveAll<IDailyKLineSource>();
            if (permits is not null)
                services.AddSingleton(permits);
            if (bars is not null)
                services.AddSingleton(bars);
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        }));

    // WebApplicationFactory の TestServer 越しに h2c を張る（実ポートを開かずに gRPC を通す。段 1〜5 と同じ）。
    private static Proto.OrderExecutionRead.OrderExecutionReadClient Grpc(
        WebApplicationFactory<Program> factory, string? roles, string? azp = null)
    {
        HttpMessageHandler handler = factory.Server.CreateHandler();
        if (roles is not null)
            handler = new RolesHeaderHandler(handler, roles, azp);
        return new(GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = handler }));
    }

    private static HttpClient Rest(WebApplicationFactory<Program> factory, string roles = ServiceRole)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    private static async Task<T> RestView<T>(HttpClient rest, string path)
    {
        var response = await rest.GetAsync(path, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<T>(Web, TestContext.Current.CancellationToken))!;
    }

    private static Proto.GetShortPermitRequest ShortPermitRequest() => new() { Symbol = "AAPL", Market = Proto.Market.UnitedStates };

    private static Proto.GetDailyBarsRequest DailyBarsRequest() =>
        new() { Symbol = "AAPL", Market = Proto.Market.UnitedStates, From = "2026-08-14", To = "2026-09-28" };

    private static Proto.GetInFlightClosesRequest InFlightRequest() =>
        new() { Symbol = "AAPL", Market = Proto.Market.UnitedStates, CloseSide = Proto.TradeSide.Sell };

    // ---- T-10-2900〜2902: REST と同じ値（同じ照会サービス） ----

    [Fact]
    public async Task T_10_2900_借株可否は_REST_と同じ値で_照会は同じサービスを通る()
    {
        await using var factory = new ExecutionWorkerWebApplicationFactory();
        var permits = new Permit(false);
        using var wired = Wire(factory, permits: permits);

        var actual = await Grpc(wired, ServiceRole).GetShortPermitAsync(ShortPermitRequest(), cancellationToken: TestContext.Current.CancellationToken);
        var expected = await RestView<ShortPermitView>(Rest(wired), "/order-execution/short-permit?symbol=AAPL&market=1");

        expected.Status.Should().Be(ShortPermitStatus.NotPermitted, "既定の値どうしの一致は何も証明しない");
        actual.Should().Be(OrderExecutionReadWireMapping.ToProto(expected));
        actual.Status.Should().Be(Proto.ShortPermitState.NotPermitted);
        permits.Calls.Should().Be(1, "2 回目（REST）は同じ照会サービスのキャッシュから返る＝照会の規則は 1 つ");
    }

    [Fact]
    public async Task T_10_2901_日足は_REST_と同じ値で足も価格も写し漏れが無い()
    {
        await using var factory = new ExecutionWorkerWebApplicationFactory();
        var bars = new DailyBarsQueryServiceTests.FakeSource(_ => new DailyKLineFetch(true, [Bar1, Bar2], null, 3, 297));
        using var wired = Wire(factory, bars: bars);

        var actual = await Grpc(wired, ServiceRole).GetDailyBarsAsync(DailyBarsRequest(), cancellationToken: TestContext.Current.CancellationToken);
        var expected = await RestView<DailyBarsView>(
            Rest(wired), "/order-execution/daily-bars?symbol=AAPL&market=1&from=2026-08-14&to=2026-09-28");

        expected.Bars.Should().HaveCount(2);
        actual.Should().Be(OrderExecutionReadWireMapping.ToProto(expected));
        actual.Status.Should().Be(Proto.DailyBarsState.Available);
        actual.Bars[1].Close.Should().Be("254.125", "価格は不変文化の 10 進文字列");
        actual.Bars[1].Date.Should().Be("2026-09-28");
        actual.Bars[1].Volume.Should().Be(52_345_678);
        bars.Requests.Should().HaveCount(2).And.AllSatisfy(r => r.Should().Be(("AAPL", new DateOnly(2026, 8, 14), new DateOnly(2026, 9, 28))));
    }

    // 内蔵 paper の本物の組み立て（建玉照会を持たない）では、処理中の決済は Unknown（broker-not-supported）。REST と同じ値。
    [Fact]
    public async Task T_10_2902_処理中の決済は_REST_と同じ値()
    {
        await using var factory = new ExecutionWorkerWebApplicationFactory();
        using var wired = Wire(factory);

        var actual = await Grpc(wired, ServiceRole).GetInFlightClosesAsync(InFlightRequest(), cancellationToken: TestContext.Current.CancellationToken);
        var expected = await RestView<InFlightClosesView>(
            Rest(wired), "/order-execution/in-flight-closes?symbol=AAPL&market=1&closeSide=1");

        actual.Should().Be(OrderExecutionReadWireMapping.ToProto(expected));
        actual.Status.Should().Be(Proto.InFlightClosesState.Unknown, "C# の Unknown（0）は線上の UNSPECIFIED（0）ではなく UNKNOWN");
        actual.UnknownReason.Should().Be(InFlightClosesUnknownReasons.BrokerNotSupported);
        actual.CloseSide.Should().Be(Proto.TradeSide.Sell);
    }

    // 写しは状態を**名前で**運ぶ（C# の 0 を線上の 0＝UNSPECIFIED にしない）。覆う・覆わない・注文 ID・株数も落とさない。
    [Fact]
    public void T_10_2902_写しは状態を名前で運び_受け手が読む項目を落とさない()
    {
        OrderExecutionReadWireMapping.ToProto(new ShortPermitView("AAPL", Market.UnitedStates, ShortPermitStatus.Unknown, "rate-limited", null))
            .Should().Be(new Proto.GetShortPermitResponse
            {
                Symbol = "AAPL",
                Market = Proto.Market.UnitedStates,
                Status = Proto.ShortPermitState.Unknown,
                UnknownReason = "rate-limited",
            });
        OrderExecutionReadWireMapping.ToProto(new ShortPermitView("7203", Market.Japan, ShortPermitStatus.Permitted, null, DateTimeOffset.UnixEpoch))
            .Should().Be(new Proto.GetShortPermitResponse { Symbol = "7203", Market = Proto.Market.Japan, Status = Proto.ShortPermitState.Permitted });
        OrderExecutionReadWireMapping.ToProto(new DailyBarsView(
                "AAPL", Market.UnitedStates, DailyBarsStatus.Unavailable, DailyBarsUnavailableReasons.RateLimited,
                new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 2), []))
            .Should().Be(new Proto.GetDailyBarsResponse
            {
                Symbol = "AAPL",
                Market = Proto.Market.UnitedStates,
                Status = Proto.DailyBarsState.Unavailable,
                UnavailableReason = DailyBarsUnavailableReasons.RateLimited,
            });

        var covered = OrderExecutionReadWireMapping.ToProto(new InFlightClosesView(
            "AAPL", Market.UnitedStates, TradeSide.Buy, InFlightClosesStatus.Covered, null, 10, 10, ["o-1", "o-2"], [Guid.NewGuid()]));
        covered.Status.Should().Be(Proto.InFlightClosesState.Covered);
        covered.CloseSide.Should().Be(Proto.TradeSide.Buy);
        covered.InFlightQuantity.Should().Be(10);
        covered.OrderIds.Should().Equal("o-1", "o-2");
        covered.HasUnknownReason.Should().BeFalse();
        OrderExecutionReadWireMapping.ToProto(new InFlightClosesView(
                "AAPL", Market.UnitedStates, TradeSide.Sell, InFlightClosesStatus.NotCovered, null, 5, 0, [], []))
            .Status.Should().Be(Proto.InFlightClosesState.NotCovered);
    }

    // ---- T-10-2903: 入力の検証は REST と同じ（値域外は照会しない） ----

    public static TheoryData<string, string> InvalidCases() => new()
    {
        { "short-permit", "symbol なし" },
        { "short-permit", "market 未指定" },
        { "daily-bars", "symbol なし" },
        { "daily-bars", "symbol が長すぎる" },
        { "daily-bars", "market 未指定" },
        { "daily-bars", "from なし" },
        { "daily-bars", "日付が読めない" },
        { "daily-bars", "逆順" },
        { "daily-bars", "400 日を超える" },
        { "in-flight-closes", "symbol なし" },
        { "in-flight-closes", "market 未指定" },
        { "in-flight-closes", "方向 未指定" },
    };

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public async Task T_10_2903_必須の欠落と値域外は_INVALID_ARGUMENT_でブローカーへ照会しない(string rpc, string kind)
    {
        await using var factory = new ExecutionWorkerWebApplicationFactory();
        var permits = new Permit(true);
        var bars = new DailyBarsQueryServiceTests.FakeSource(_ => new DailyKLineFetch(true, [Bar1], null, null, null));
        using var wired = Wire(factory, permits, bars);
        var client = Grpc(wired, ServiceRole);

        Func<Task> act = rpc switch
        {
            "short-permit" => async () => await client.GetShortPermitAsync(kind switch
            {
                "symbol なし" => new Proto.GetShortPermitRequest { Market = Proto.Market.UnitedStates },
                _ => new Proto.GetShortPermitRequest { Symbol = "AAPL" },
            }),
            "daily-bars" => async () =>
            {
                var r = DailyBarsRequest();
                switch (kind)
                {
                    case "symbol なし": r.ClearSymbol(); break;
                    case "symbol が長すぎる": r.Symbol = new string('A', DailyBarsEndpoint.MaxSymbolLength + 1); break;
                    case "market 未指定": r.Market = Proto.Market.Unspecified; break;
                    case "from なし": r.ClearFrom(); break;
                    case "日付が読めない": r.To = "2026/09/28"; break;
                    case "逆順": r.From = "2026-09-29"; break;
                    case "400 日を超える": r.From = "2025-08-24"; break;
                }

                await client.GetDailyBarsAsync(r);
            }
            ,
            _ => async () =>
            {
                var r = InFlightRequest();
                switch (kind)
                {
                    case "symbol なし": r.ClearSymbol(); break;
                    case "market 未指定": r.Market = Proto.Market.Unspecified; break;
                    case "方向 未指定": r.CloseSide = Proto.TradeSide.Unspecified; break;
                }

                await client.GetInFlightClosesAsync(r);
            }
            ,
        };

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        permits.Calls.Should().Be(0);
        bars.Requests.Should().BeEmpty();
    }

    // 境界の陽性対照: 400 日ちょうどは通る（REST の T-10-1837 と同じ境界）。
    [Fact]
    public async Task T_10_2903_期間の上限ちょうどは照会する()
    {
        await using var factory = new ExecutionWorkerWebApplicationFactory();
        var bars = new DailyBarsQueryServiceTests.FakeSource(_ => new DailyKLineFetch(true, [Bar1], null, null, null));
        using var wired = Wire(factory, bars: bars);
        var request = DailyBarsRequest();
        request.From = "2025-08-25";

        var response = await Grpc(wired, ServiceRole).GetDailyBarsAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        response.Status.Should().Be(Proto.DailyBarsState.Available);
        bars.Requests.Should().ContainSingle();
    }

    // ---- T-10-2904: 門（deny-by-default の GrpcOwnerOrService） ----

    [Fact]
    public async Task T_10_2904_資格情報が無ければ_UNAUTHENTICATED_で_REST_は401()
    {
        await using var factory = new ExecutionWorkerWebApplicationFactory();
        var permits = new Permit(true);
        using var wired = Wire(factory, permits: permits);

        var act = async () => await Grpc(wired, roles: null).GetShortPermitAsync(ShortPermitRequest());

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
        (await wired.CreateClient().GetAsync("/order-execution/short-permit?symbol=AAPL&market=1", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "REST も同じ");
        permits.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("some-unrelated-role", null)]
    [InlineData(OwnerRole, null)]                       // 人の利用者のトークン（azp なし）は gRPC 面を通らない
    [InlineData(OwnerRole, "ai-stock-trading-bff")]     // ボット以外のクライアント
    public async Task T_10_2904_ロールが足りない_またはボット以外の所有者は_PERMISSION_DENIED(string roles, string? azp)
    {
        await using var factory = new ExecutionWorkerWebApplicationFactory();
        var permits = new Permit(true);
        using var wired = Wire(factory, permits: permits);
        var client = Grpc(wired, roles, azp);

        foreach (var act in new Func<Task>[]
                 {
                     async () => await client.GetShortPermitAsync(ShortPermitRequest()),
                     async () => await client.GetDailyBarsAsync(DailyBarsRequest()),
                     async () => await client.GetInFlightClosesAsync(InFlightRequest()),
                 })
        {
            (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        }

        permits.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(ServiceRole, null)]
    [InlineData(OwnerRole, BotClient)]
    public async Task T_10_2904_サービスとボットの所有者は読める(string roles, string? azp)
    {
        await using var factory = new ExecutionWorkerWebApplicationFactory();
        using var wired = Wire(factory, permits: new Permit(true));

        var response = await Grpc(wired, roles, azp).GetShortPermitAsync(ShortPermitRequest(), cancellationToken: TestContext.Current.CancellationToken);

        response.Status.Should().Be(Proto.ShortPermitState.Permitted);
    }

    private sealed class RolesHeaderHandler(HttpMessageHandler inner, string roles, string? azp) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation(TestAuthHandler.RolesHeader, roles);
            if (azp is not null)
                request.Headers.TryAddWithoutValidation(TestAuthHandler.AzpHeader, azp);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class Permit(bool? answer) : IShortPermitSource
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<bool?> GetShortPermitAsync(string symbol, Market market, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(answer);
        }
    }
}
