extern alias RiskManagementWorker;

using System.Net;
using System.Reflection;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RiskManagementWorker::RiskManagementService.Features.RiskManagement.GetEntryBlockers;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using TradeDecisionService.Infrastructure.ExternalServices;
using Wolverine;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;
using RiskReadWireMapping = RiskManagementWorker::RiskManagementService.Features.RiskManagement.RiskReadWireMapping;

namespace TradeDecisionService.Tests.Infrastructure.ExternalServices;

// T-10-1788〜T-10-1790・T-10-1794, FR-10, FR-04, #1113, IADR-0463 決定 3・4, IADR-0420, IADR-0427:
// 判断側の新規建ての可否の照会（Http / Grpc / NoOp）と本番の配線。
// 🔴 失敗・不正な応答は **null（不明）**＝判断は LLM を呼ぶ。塞がっていると**読み違えて見送る**向きの誤り
// （別銘柄の応答・欠落・未知の理由）を必ず不明へ倒す。
public class EntryBlockersProviderTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? Requested { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private static HttpEntryBlockersProvider Http(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://risk") }, NullLogger<HttpEntryBlockersProvider>.Instance);

    // 送り手の本物の型（位置引数）を web 既定で直列化した本文。
    private static string Body(EntryBlockersView view) => JsonSerializer.Serialize(view, Web);

    // ---- T-10-1788: REST ----

    [Fact]
    public async Task T_10_1788_送り手の応答を方向別に読み_銘柄と市場を問いに載せる()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Body(new EntryBlockersView(
            "BRK.B", Market.UnitedStates,
            [RejectionReason.KillSwitchActive, RejectionReason.StoppedOutSameDay], [RejectionReason.KillSwitchActive])));

        var read = await Http(handler).GetAsync("BRK.B", Market.UnitedStates);

        read.Should().NotBeNull();
        read!.LongSide.Should().Equal(RejectionReason.KillSwitchActive, RejectionReason.StoppedOutSameDay);
        read.ShortSide.Should().Equal(RejectionReason.KillSwitchActive);
        read.ForEntry(TradeSide.Buy).Should().BeSameAs(read.LongSide);
        read.ForEntry(TradeSide.Sell).Should().BeSameAs(read.ShortSide);
        handler.Requested!.AbsolutePath.Should().Be("/risk-controls/entry-blockers");
        handler.Requested.Query.Should().Be("?symbol=BRK.B&market=1");
    }

    [Fact]
    public async Task T_10_1788_空の一覧は確定する拒否は無い()
    {
        var read = await Http(new StubHandler(HttpStatusCode.OK, Body(new EntryBlockersView("AAPL", Market.UnitedStates, [], []))))
            .GetAsync("AAPL", Market.UnitedStates);

        read.Should().NotBeNull();
        read!.LongSide.Should().BeEmpty();
        read.ShortSide.Should().BeEmpty();
    }

    public static TheoryData<string, string> Malformed() => new()
    {
        { "別の銘柄の答え", """{"symbol":"MSFT","market":1,"longSide":[9],"shortSide":[]}""" },
        { "別の市場の答え", """{"symbol":"AAPL","market":0,"longSide":[9],"shortSide":[]}""" },
        { "銘柄の欠落（送り手の改名）", """{"ticker":"AAPL","market":1,"longSide":[9],"shortSide":[]}""" },
        { "市場の欠落", """{"symbol":"AAPL","longSide":[9],"shortSide":[]}""" },
        { "買いの一覧の欠落", """{"symbol":"AAPL","market":1,"shortSide":[]}""" },
        { "売りの一覧の欠落", """{"symbol":"AAPL","market":1,"longSide":[9]}""" },
        { "未知の理由", """{"symbol":"AAPL","market":1,"longSide":[9999],"shortSide":[]}""" },
        { "null の理由", """{"symbol":"AAPL","market":1,"longSide":[null],"shortSide":[]}""" },
        { "本文が null", "null" },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task T_10_1788_問いに答えていない応答は不明(string how, string body)
    {
        (await Http(new StubHandler(HttpStatusCode.OK, body)).GetAsync("AAPL", Market.UnitedStates)).Should().BeNull(how);
    }

    [Fact]
    public async Task T_10_1788_失敗は不明()
    {
        (await Http(new StubHandler(HttpStatusCode.InternalServerError, "")).GetAsync("AAPL", Market.UnitedStates))
            .Should().BeNull("非 2xx");
        (await Http(new StubHandler(HttpStatusCode.OK, "{not json")).GetAsync("AAPL", Market.UnitedStates))
            .Should().BeNull("読めない本文");
        (await Http(new ThrowingHandler(new HttpRequestException("届かない"))).GetAsync("AAPL", Market.UnitedStates))
            .Should().BeNull("例外");
        (await Http(new ThrowingHandler(new TaskCanceledException("タイムアウト"))).GetAsync("AAPL", Market.UnitedStates))
            .Should().BeNull("タイムアウト（呼び出し側のキャンセルではない）");
        (await new NoOpEntryBlockersProvider().GetAsync("AAPL", Market.UnitedStates)).Should().BeNull("未結線は常に不明");
    }

    // ---- T-10-1789: gRPC（実 h2c） ----

    private static async Task<(ServiceProvider Sp, GrpcEntryBlockersProvider Provider)> GrpcAsync(string address)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["RiskManagement:Grpc"] = address })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingRiskManagementGrpc(config);
        var sp = services.BuildServiceProvider();
        await Task.CompletedTask;
        return (sp, new GrpcEntryBlockersProvider(
            sp.GetRequiredService<RiskManagementGrpcTransport>(), sp.GetRequiredService<ILogger<GrpcEntryBlockersProvider>>()));
    }

    [Fact]
    public async Task T_10_1789_送り手の写しを方向別に読み_銘柄と市場を線に載せる()
    {
        var view = new EntryBlockersView(
            "7203", Market.Japan, [RejectionReason.MaxPositionsExceeded, RejectionReason.DailyLossLimitReached], []);
        var behavior = new RiskReadStubBehavior { EntryBlockers = (_, _) => Task.FromResult(RiskReadWireMapping.ToProto(view)) };
        await using var host = await RiskReadStubHost.StartAsync(behavior);
        var (sp, provider) = await GrpcAsync(host.Address);
        await using (sp)
        {
            var read = await provider.GetAsync("7203", Market.Japan);

            read.Should().NotBeNull();
            read!.LongSide.Should().Equal(view.LongSide);
            read.ShortSide.Should().BeEmpty();
            behavior.LastEntryBlockersRequest!.Symbol.Should().Be("7203");
            behavior.LastEntryBlockersRequest.Market.Should().Be(Proto.Market.Japan, "日本は線上で 1（未指定の 0 ではない）");
        }
    }

    [Theory]
    [InlineData("買いの入れ物の欠落")]
    [InlineData("売りの入れ物の欠落")]
    [InlineData("未指定の理由")]
    [InlineData("未知の番号の理由")]
    [InlineData("銘柄の欠落")]
    [InlineData("市場が未指定")]
    [InlineData("失敗")]
    public async Task T_10_1789_欠落と失敗は不明(string how)
    {
        var response = RiskReadWireMapping.ToProto(
            new EntryBlockersView("AAPL", Market.UnitedStates, [RejectionReason.KillSwitchActive], []));
        switch (how)
        {
            case "買いの入れ物の欠落": response.LongSide = null; break;
            case "売りの入れ物の欠落": response.ShortSide = null; break;
            case "未指定の理由": response.LongSide.Reasons.Add(Proto.EntryBlocker.Unspecified); break;
            case "未知の番号の理由": response.LongSide.Reasons.Add((Proto.EntryBlocker)99); break;
            case "銘柄の欠落": response.ClearSymbol(); break;
            case "市場が未指定": response.Market = Proto.Market.Unspecified; break;
        }

        var behavior = new RiskReadStubBehavior
        {
            EntryBlockers = how == "失敗"
                ? RiskReadStubBehavior.Fails<Proto.GetEntryBlockersResponse>(StatusCode.Unavailable)
                : (_, _) => Task.FromResult(response),
        };
        await using var host = await RiskReadStubHost.StartAsync(behavior);
        var (sp, provider) = await GrpcAsync(host.Address);
        await using (sp)
        {
            (await provider.GetAsync("AAPL", Market.UnitedStates)).Should().BeNull(how);
        }
    }

    // T-10-1789: 線上の列挙はすべて名前で読める（送り手が写す全理由）。
    [Fact]
    public void T_10_1789_線上の理由はすべて名前で写る()
    {
        foreach (var wire in Enum.GetValues<Proto.EntryBlocker>().Where(v => v != Proto.EntryBlocker.Unspecified))
        {
            GrpcEntryBlockersProvider.Reason(wire).Should().NotBeNull(wire.ToString());
            GrpcEntryBlockersProvider.Reason(wire).ToString().Should().Be(wire.ToString());
        }

        GrpcEntryBlockersProvider.Reason(Proto.EntryBlocker.Unspecified).Should().BeNull();
    }

    // ---- T-10-1790: 契約（送り手の本物の型を、本番の配線のアダプタで読む。IADR-0420 決定 1） ----

    private sealed class RouteStub(string path, string body) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requested = request.RequestUri?.AbsolutePath ?? string.Empty;
            lock (Paths)
                Paths.Add(requested);
            return Task.FromResult(requested == path
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task T_10_1790_新規建ての可否は送り手の本物の型を直列化した応答から本番の配線のアダプタで読める()
    {
        var view = new EntryBlockersView(
            "AAPL", Market.UnitedStates, [RejectionReason.StoppedOutSameDay], [RejectionReason.TradingPaused]);
        var handler = new RouteStub("/risk-controls/entry-blockers", Body(view));
        using var factory = new Factory(grpc: null, riskBaseUrl: "http://risk", riskHandler: handler);

        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IEntryBlockersProvider>();
        provider.Should().BeOfType<HttpEntryBlockersProvider>("本番の配線（RiskManagement:BaseUrl あり）が選ぶ実装を試す");

        var read = await provider.GetAsync("AAPL", Market.UnitedStates);

        read.Should().NotBeNull();
        read!.LongSide.Should().Equal(RejectionReason.StoppedOutSameDay);
        read.ShortSide.Should().Equal(RejectionReason.TradingPaused);
        handler.Paths.Should().OnlyContain(p => p == "/risk-controls/entry-blockers");
    }

    // ---- T-10-1794: 本番の配線（保有照会と同じ選び方・判断サービスへ届く） ----

    [Theory]
    [InlineData(null, null, typeof(NoOpEntryBlockersProvider))]
    [InlineData(null, "http://risk", typeof(HttpEntryBlockersProvider))]
    [InlineData("grpc", "http://risk-rest-must-not-be-used", typeof(GrpcEntryBlockersProvider))]
    public async Task T_10_1794_構成で実装を選び判断サービスへ届く(string? grpc, string? riskBaseUrl, Type expected)
    {
        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior());
        using var factory = new Factory(grpc is null ? null : host.Address, riskBaseUrl);
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IEntryBlockersProvider>().Should().BeOfType(expected);

        // 🔴 登録だけでなく、判断サービスが実際に受け取っている（省略可能な引数のまま既定の NoOp に落ちていない）。
        var app = scope.ServiceProvider.GetRequiredService<TradeDecisionAppService>();
        var field = typeof(TradeDecisionAppService).GetField("_entryBlockers", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.GetValue(app).Should().BeOfType(expected);
    }

    private sealed class Factory(string? grpc, string? riskBaseUrl, HttpMessageHandler? riskHandler = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            if (grpc is not null)
                builder.UseSetting("RiskManagement:Grpc", grpc);
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["RabbitMq:ConnectionString"] = "amqp://localhost",
                    ["Otlp:Endpoint"] = "http://localhost:4317",
                };
                if (riskBaseUrl is not null)
                    settings["RiskManagement:BaseUrl"] = riskBaseUrl;
                cfg.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
            if (riskHandler is not null)
                builder.ConfigureTestServices(services =>
                    services.AddHttpClient("risk").ConfigurePrimaryHttpMessageHandler(() => riskHandler));
        }
    }
}
