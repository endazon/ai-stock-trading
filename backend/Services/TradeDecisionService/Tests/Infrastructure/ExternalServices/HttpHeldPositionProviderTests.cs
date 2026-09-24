extern alias RiskManagementWorker;

using System.Net;
using System.Text.Json;
using RiskManagementWorker::RiskManagementService.Features.RiskManagement.GetWorkingEntryOrders;
using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Xunit;
// IADR-0128: consumer は Infrastructure へ移った。相対名（Composable.Steps.*）参照をテスト本文を触らずに解決する。
using Composable = TradeDecisionService.Infrastructure;

namespace TradeDecisionService.Tests;

// FR-04, FR-05, FR-10, #292, IADR-0119: 保有建玉の同期照会（GET /risk-controls/open-positions）。
// 中核の契約は「空配列＝0（保有なし）／失敗＝null（不明）」の厳格な区別。
// 失敗を 0 へ倒すと「保有していない」と誤断定し、裸の新規売りを通してしまう。
public class HttpHeldPositionProviderTests
{
    private static HttpHeldPositionProvider Provider(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://risk") },
            NullLogger<HttpHeldPositionProvider>.Instance);

    // OpenPositionView（RiskManagement）の web 既定 JSON（camelCase・列挙は数値）。
    private const string TwoPositions = """
        [
          {"symbol":"AAPL","market":1,"side":0,"quantity":4072,"entryPrice":20.5,"stopLossPrice":19.0},
          {"symbol":"7203","market":0,"side":1,"quantity":100,"entryPrice":2500,"stopLossPrice":2600}
        ]
        """;

    [Fact]
    public async Task ロング建玉は正の数量で返る()
    {
        var handler = new StubHandler(HttpStatusCode.OK, TwoPositions);

        var held = await Provider(handler).GetSignedQuantityAsync("AAPL", Market.UnitedStates);

        held.Should().Be(4072);
        handler.LastPath.Should().Be("/risk-controls/open-positions");
    }

    [Fact]
    public async Task ショート建玉は負の数量で返る()
    {
        var held = await Provider(new StubHandler(HttpStatusCode.OK, TwoPositions))
            .GetSignedQuantityAsync("7203", Market.Japan);

        held.Should().Be(-100);
    }

    [Fact]
    public async Task 一覧に無い銘柄は保有なしのゼロ()
    {
        var held = await Provider(new StubHandler(HttpStatusCode.OK, TwoPositions))
            .GetSignedQuantityAsync("MSFT", Market.UnitedStates);

        held.Should().Be(0, "不明（null）ではなく保有なし（0）");
    }

    [Fact]
    public async Task 同一コードの別市場は数えない()
    {
        var held = await Provider(new StubHandler(HttpStatusCode.OK, TwoPositions))
            .GetSignedQuantityAsync("7203", Market.UnitedStates);

        held.Should().Be(0);
    }

    [Fact]
    public async Task 空配列は保有なしのゼロ()
    {
        // 空列は「建玉が 1 つも無い」という観測事実。null（不明）と取り違えない。
        var held = await Provider(new StubHandler(HttpStatusCode.OK, "[]"))
            .GetSignedQuantityAsync("AAPL", Market.UnitedStates);

        held.Should().Be(0);
    }

    // --- FR-04, FR-10, ADR-0003, #854, IADR-0351 決定1: 判断プロンプトへ載せる保有状況（数量・取得単価・損切りライン） ---

    [Fact]
    public async Task 保有状況は数量と平均取得単価と記録上の損切りラインを返す()
    {
        var handler = new StubHandler(HttpStatusCode.OK, TwoPositions);

        var held = await Provider(handler).GetPositionAsync("AAPL", Market.UnitedStates);

        held.Should().Be(new HeldPosition(4072, 20.5m, 19.0m));
        handler.LastPath.Should().Be("/risk-controls/open-positions");
    }

    [Fact]
    public async Task ショートの保有状況は負の数量で返る()
    {
        var held = await Provider(new StubHandler(HttpStatusCode.OK, TwoPositions))
            .GetPositionAsync("7203", Market.Japan);

        held.Should().Be(new HeldPosition(-100, 2500m, 2600m));
    }

    // 🔴 中核の区別: 一覧に無い＝保有なし（None）／失敗＝null（不明）。不明を保有なしへ倒さない。
    [Fact]
    public async Task 一覧に無い銘柄の保有状況は保有なしであり不明ではない()
    {
        var held = await Provider(new StubHandler(HttpStatusCode.OK, TwoPositions))
            .GetPositionAsync("MSFT", Market.UnitedStates);

        held.Should().Be(HeldPosition.None);
        held!.IsHeld.Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task 非2xx_の保有状況は不明であり保有なしではない(HttpStatusCode status)
    {
        var held = await Provider(new StubHandler(status, "[]")).GetPositionAsync("AAPL", Market.UnitedStates);

        held.Should().BeNull();
    }

    [Fact]
    public async Task 不正な応答の保有状況は不明()
    {
        var held = await Provider(new StubHandler(HttpStatusCode.OK, "not-json"))
            .GetPositionAsync("AAPL", Market.UnitedStates);

        held.Should().BeNull();
    }

    // 🔴 価格の項目を持たない・正でない応答を 0 と読まない（取得単価 0 は含み損益を、損切りライン 0 は「未到達」を捏造する）。
    [Fact]
    public async Task 価格の項目が無い_または正でない応答は価格だけ不明にする()
    {
        const string body = """
            [
              {"symbol":"AAPL","market":1,"side":0,"quantity":10},
              {"symbol":"MSFT","market":1,"side":0,"quantity":5,"entryPrice":0,"stopLossPrice":0}
            ]
            """;

        var aapl = await Provider(new StubHandler(HttpStatusCode.OK, body)).GetPositionAsync("AAPL", Market.UnitedStates);
        var msft = await Provider(new StubHandler(HttpStatusCode.OK, body)).GetPositionAsync("MSFT", Market.UnitedStates);

        aapl.Should().Be(new HeldPosition(10, null, null));
        msft.Should().Be(new HeldPosition(5, null, null));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task 非2xx_は不明(HttpStatusCode status)
    {
        var held = await Provider(new StubHandler(status, "")).GetSignedQuantityAsync("AAPL", Market.UnitedStates);

        held.Should().BeNull("失敗を 0 へ倒すと裸の新規売りを通してしまう");
    }

    [Fact]
    public async Task 不正な応答は不明()
    {
        var held = await Provider(new StubHandler(HttpStatusCode.OK, "null"))
            .GetSignedQuantityAsync("AAPL", Market.UnitedStates);

        held.Should().BeNull();
    }

    [Fact]
    public async Task 例外は不明()
    {
        var held = await Provider(new ThrowingHandler()).GetSignedQuantityAsync("AAPL", Market.UnitedStates);

        held.Should().BeNull();
    }

    // --- FR-04, FR-10, #934, IADR-0390 決定2: 未約定の新規建て注文（GET /risk-controls/working-entry-orders） ---
    // T-10-719: 一致する行だけを採る／空＝無い／失敗・不正応答＝不明（null）。不明を「無い」へ倒すと「保有なし」の前提で重ね買いする。

    // WorkingEntryOrderView（RiskManagement）の web 既定 JSON（camelCase・列挙は数値）。
    private const string WorkingOrders = """
        [
          {"decisionId":"6d3c4a5e-0000-0000-0000-000000000001","symbol":"AAPL","market":1,"side":0,"remainingQuantity":715,"price":337.63,"approvedAt":"2026-09-23T13:46:45+00:00"},
          {"decisionId":"6d3c4a5e-0000-0000-0000-000000000002","symbol":"AAPL","market":1,"side":0,"remainingQuantity":100,"price":338.1,"approvedAt":"2026-09-23T13:56:45+00:00"},
          {"decisionId":"6d3c4a5e-0000-0000-0000-000000000003","symbol":"7203","market":0,"side":0,"remainingQuantity":100,"price":2500,"approvedAt":"2026-09-23T01:00:00+00:00"}
        ]
        """;

    [Fact]
    public async Task 未約定の新規建ては銘柄と市場が一致する行だけを残数量つきで返す()
    {
        var handler = new StubHandler(HttpStatusCode.OK, WorkingOrders);

        var working = await Provider(handler).GetWorkingEntryOrdersAsync("AAPL", Market.UnitedStates);

        handler.LastPath.Should().Be("/risk-controls/working-entry-orders");
        working.Should().NotBeNull();
        working!.Orders.Should().Equal(
            new WorkingEntryOrder(TradeSide.Buy, 715, 337.63m, new DateTimeOffset(2026, 9, 23, 13, 46, 45, TimeSpan.Zero)),
            new WorkingEntryOrder(TradeSide.Buy, 100, 338.1m, new DateTimeOffset(2026, 9, 23, 13, 56, 45, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("[]", "AAPL", Market.UnitedStates)]
    [InlineData(WorkingOrders, "MSFT", Market.UnitedStates)]
    [InlineData(WorkingOrders, "7203", Market.UnitedStates)] // 同一コードの別市場は数えない
    public async Task 該当が無ければ未約定は無いであり不明ではない(string body, string symbol, Market market)
    {
        var working = await Provider(new StubHandler(HttpStatusCode.OK, body)).GetWorkingEntryOrdersAsync(symbol, market);

        working.Should().Be(WorkingEntryOrders.None);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task 未約定の照会が非2xxなら不明(HttpStatusCode status)
    {
        var working = await Provider(new StubHandler(status, "")).GetWorkingEntryOrdersAsync("AAPL", Market.UnitedStates);

        working.Should().BeNull("失敗を「無い」へ倒すと、板に残った指値を知らないまま重ねて買う");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""[{"symbol":"AAPL","market":1,"side":0,"remainingQuantity":0,"price":337.63,"approvedAt":"2026-09-23T13:46:45+00:00"}]""")]
    public async Task 未約定の応答が解釈できなければ不明(string body)
    {
        var working = await Provider(new StubHandler(HttpStatusCode.OK, body)).GetWorkingEntryOrdersAsync("AAPL", Market.UnitedStates);

        working.Should().BeNull();
    }

    [Fact]
    public async Task 未約定の照会の例外は不明()
    {
        var working = await Provider(new ThrowingHandler()).GetWorkingEntryOrdersAsync("AAPL", Market.UnitedStates);

        working.Should().BeNull();
    }

    // 🔴 T-10-744, FR-04, FR-10, #934, IADR-0390（PR #940 監査・契約の fail-open）:
    // **送り手の本物の型（`WorkingEntryOrderView`）を web 既定 JSON で直列化し、アダプタがそれを読めることを固定する。**
    // 上の手書き JSON だけでは、リスク管理側で項目名を変えても（例: `Symbol` → `Ticker`）両スイートとも緑のままで、
    // 実行時はアダプタの一致が 0 件＝「無い」になり、板に指値が残っているのにプロンプトは「保有: なし」と書く
    // （#934 の実測そのもの）。本テストは改名を赤で止める（変異注入で実測）。
    [Fact]
    public async Task 未約定は送り手の本物の型を直列化した応答から読める()
    {
        var approvedAt = new DateTimeOffset(2026, 9, 23, 13, 46, 45, TimeSpan.Zero);
        IReadOnlyList<WorkingEntryOrderView> views =
        [
            new(Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, 715, 337.63m, approvedAt),
            new(Guid.NewGuid(), "7203", Market.Japan, TradeSide.Buy, 100, 2500m, approvedAt.AddHours(-12)),
        ];
        // リスク管理の Minimal API（Results.Ok）と同じ web 既定（camelCase・列挙は数値）。
        var body = JsonSerializer.Serialize(views, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var aapl = await Provider(new StubHandler(HttpStatusCode.OK, body)).GetWorkingEntryOrdersAsync("AAPL", Market.UnitedStates);
        var toyota = await Provider(new StubHandler(HttpStatusCode.OK, body)).GetWorkingEntryOrdersAsync("7203", Market.Japan);
        var none = await Provider(new StubHandler(HttpStatusCode.OK, body)).GetWorkingEntryOrdersAsync("MSFT", Market.UnitedStates);

        aapl.Should().NotBeNull();
        aapl!.Orders.Should().Equal(new WorkingEntryOrder(TradeSide.Buy, 715, 337.63m, approvedAt));
        toyota.Should().NotBeNull();
        toyota!.Orders.Should().Equal(new WorkingEntryOrder(TradeSide.Buy, 100, 2500m, approvedAt.AddHours(-12)));
        none.Should().Be(WorkingEntryOrders.None, "一致しない銘柄は「無い」（送り手の型のままでも区別が保たれる）");
    }

    // 🔴 T-10-745, #934, IADR-0390（PR #940 監査）: **銘柄・市場の無い行は「一致しない」と読まない。**
    // その行が判断対象かどうか判らないため、応答全体を不明（null）にする。項目の欠けた一致行（方向・残数量・価格・承認時刻）も同じ。
    [Theory]
    [InlineData("""[{"market":1,"side":0,"remainingQuantity":715,"price":337.63,"approvedAt":"2026-09-23T13:46:45+00:00"}]""")]
    [InlineData("""[{"symbol":null,"market":1,"side":0,"remainingQuantity":715,"price":337.63,"approvedAt":"2026-09-23T13:46:45+00:00"}]""")]
    [InlineData("""[{"symbol":"","market":1,"side":0,"remainingQuantity":715,"price":337.63,"approvedAt":"2026-09-23T13:46:45+00:00"}]""")]
    [InlineData("""[{"ticker":"AAPL","market":1,"side":0,"remainingQuantity":715,"price":337.63,"approvedAt":"2026-09-23T13:46:45+00:00"}]""")]
    [InlineData("""[{"symbol":"AAPL","side":0,"remainingQuantity":715,"price":337.63,"approvedAt":"2026-09-23T13:46:45+00:00"}]""")]
    [InlineData("""[{"symbol":"AAPL","market":1,"remainingQuantity":715,"price":337.63,"approvedAt":"2026-09-23T13:46:45+00:00"}]""")]
    [InlineData("""[{"symbol":"AAPL","market":1,"side":0,"price":337.63,"approvedAt":"2026-09-23T13:46:45+00:00"}]""")]
    [InlineData("""[{"symbol":"AAPL","market":1,"side":0,"remainingQuantity":715,"approvedAt":"2026-09-23T13:46:45+00:00"}]""")]
    [InlineData("""[{"symbol":"AAPL","market":1,"side":0,"remainingQuantity":715,"price":337.63}]""")]
    [InlineData("""[null]""")]
    public async Task 未約定の行に銘柄や必要な項目が無ければ不明であり無いではない(string body)
    {
        var working = await Provider(new StubHandler(HttpStatusCode.OK, body)).GetWorkingEntryOrdersAsync("AAPL", Market.UnitedStates);

        working.Should().BeNull("項目の欠落を「無い」と読むと、板に残った指値を知らないまま重ねて買う");
    }

    // T-10-747, #934, IADR-0390（PR #940 監査・非ブロッカー）: 打ち切り・壊れた JSON・空の本文も**不明**であり「無い」ではない。
    [Theory]
    [InlineData("")]
    [InlineData("""[{"symbol":"AAPL" """)]
    [InlineData("{}")]
    public async Task 未約定の応答が空や壊れたJSONなら不明(string body)
    {
        var working = await Provider(new StubHandler(HttpStatusCode.OK, body)).GetWorkingEntryOrdersAsync("AAPL", Market.UnitedStates);

        working.Should().BeNull();
    }

    [Fact]
    public async Task 未約定の照会が打ち切られたら不明()
    {
        // #885, IADR-0379 と同じ形: 応答しない上流を上限で打ち切る（壁時計どうしの競争にしない）。
        var handler = new NeverRespondingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://risk"), Timeout = TimeSpan.FromMilliseconds(50) };
        var provider = new HttpHeldPositionProvider(http, NullLogger<HttpHeldPositionProvider>.Instance);

        // Guard は「打ち切りが効かない」ときに黙って固まらないための上限であり、合否の基準ではない。
        var working = await provider.GetWorkingEntryOrdersAsync("AAPL", Market.UnitedStates).WaitAsync(Guard);

        working.Should().BeNull("打ち切りを「無い」と読まない");
        (await handler.Cancellation.WaitAsync(Guard)).Should().BeTrue("上限に達した要求は打ち切られる（応答は返っていない）");
    }

    [Fact]
    public async Task 未結線のNoOpでは未約定は不明()
    {
        var working = await new NoOpHeldPositionProvider().GetWorkingEntryOrdersAsync("AAPL", Market.UnitedStates);

        working.Should().BeNull("未結線は「照会していない」であり「無い」ではない");
    }

    // --- 配線（RiskManagement:BaseUrl の有無で切り替わる） ---

    [Fact]
    public void BaseUrl未設定は安全既定のNoOp()
    {
        using var factory = new Factory(riskBaseUrl: null);
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IHeldPositionProvider>();
        provider.Should().BeOfType<TradeDecisionService.Infrastructure.ExternalServices.NoOpHeldPositionProvider>();
        // #865, IADR-0358: 未結線は IsEnabled=false。ここが true になると既定構成の新規建てが一律に止まる。
        provider.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void BaseUrl設定時はリスク管理を同期照会するHttp実装()
    {
        using var factory = new Factory(riskBaseUrl: "http://risk");
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IHeldPositionProvider>();
        provider.Should().BeOfType<HttpHeldPositionProvider>();
        // #865, IADR-0358: 実結線は IsEnabled=true。以後の「不明」は照会したが分からなかったことを意味する。
        provider.IsEnabled.Should().BeTrue();
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    // 打ち切りが効かなくなったときに、黙って固まる代わりに理由付きで赤くするための上限（合否の基準ではない）。
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private sealed class NeverRespondingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _cancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // 要求が打ち切られたか（true＝上限で切られた）。
        public Task<bool> Cancellation => _cancellation.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _cancellation.TrySetResult(cancellationToken.IsCancellationRequested);
            }

            throw new InvalidOperationException("到達しない（無期限待ちは打ち切りでしか終わらない）。");
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("接続できません");
    }

    private sealed class Factory(string? riskBaseUrl) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
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
            builder.ConfigureServices(services =>
            {
                // ADR-0013, IADR-0129, #354: 実 RabbitMQ を避けて Wolverine の外部トランスポートを無効化する
                // （ハンドラの発見は Program.cs 側の配線が担う）。
                services.DisableAllExternalWolverineTransports();
            });
        }
    }
}
