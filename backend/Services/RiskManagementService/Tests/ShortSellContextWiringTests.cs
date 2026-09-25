extern alias OrderExecutionWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using OrderExecutionWorker::OrderExecutionService.Features.OrderExecution.QueryShortPermit;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10 (1)(3)(6), UC-06, ADR-0016 決定2(a)・決定3・決定9, #967, IADR-0425 決定5〜8（T-10-1027〜T-10-1029）:
// **本番の組み立て（Program.cs）を通して**、空売り文脈の供給元が結線され、審査がその文脈を判定コアへ渡していること。
//
// 🔴 なぜ要るか: 単体のテストは OrderScreeningService・ShortSellContextSupplier を自分で組むため、Program.cs から
// 供給元を外しても（照会先を常に「分からない」へ差し替えても）緑のままである。過去 4 本の PR が「配線を外しても全緑」だった。
// 本テストは「本番の DI → 本番の Wolverine ハンドラ（TradeDecisionMadeHandler）→ 本番の審査 → 本番の受け手（HttpShortSellBorrowSource）」
// を通しで見る。差し替えるのは DB（InMemory）・外部トランスポート・"order-execution" の HttpClient の一次ハンドラ（送り手の本物の型の
// 応答を返す）・手元の現在値（本番の QuoteCache へ直接置く）だけである。
public class ShortSellContextWiringTests
{
    // 発注執行の Minimal API（Results.Ok）と同じ web 既定。
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // 空売り 1 件（逆指値つき・株価下限 $5 以上）。基準資金は factory の既定 3,000 USD（1 銘柄の上限 10% ＝ 300）。
    private static OrderIntent ShortEntry(int quantity, decimal price) =>
        new("MSFT", Market.UnitedStates, TradeSide.Sell, ProductType.ShortSell, BrokerProvider.InternalPaper,
            quantity, price, PositionEffect.Open, StopLossPrice: price * 1.1m);

    private static WebApplicationFactory<Program> Wire(
        RiskWorkerWebApplicationFactory factory, ShortPermitStatus? permit, out PermitStub stub)
    {
        var handler = new PermitStub(permit ?? ShortPermitStatus.Unknown);
        stub = handler;
        return factory.WithWebHostBuilder(b =>
        {
            if (permit is not null)
                b.UseSetting("OrderExecution:BaseUrl", "http://order-execution");
            b.ConfigureServices(services =>
                services.AddHttpClient("order-execution").ConfigurePrimaryHttpMessageHandler(() => handler));
        });
    }

    private static async Task<IReadOnlyList<RejectionReason>> RejectionReasonsAsync(
        WebApplicationFactory<Program> wired, OrderIntent intent)
    {
        var session = await wired.Services.ExecuteAndWaitForTestAsync(async () =>
        {
            using var scope = wired.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IMessageBus>()
                .InvokeAsync(new TradeDecisionMade(Guid.NewGuid(), intent, "判断", DateTimeOffset.UtcNow));
        });

        session.Sent.MessagesOf<OrderApproved>().Should().BeEmpty("空売りは料率が供給されない間は通らない");
        return session.Sent.MessagesOf<OrderRejected>().Should().ContainSingle().Subject.Reasons;
    }

    /// <summary>T-10-1027: 照会先を構成すると本物の受け手（HttpShortSellBorrowSource）が、無ければ「分からない」の供給が解決される。</summary>
    [Fact]
    public async Task 本番の組み立ては照会先があれば本物の受け手を無ければ分からないの供給を解決する()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        using (var wired = Wire(factory, ShortPermitStatus.Permitted, out _))
        using (var scope = wired.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IShortSellBorrowSource>().Should().BeOfType<HttpShortSellBorrowSource>();
            scope.ServiceProvider.GetRequiredService<ShortSellContextSupplier>().Should().NotBeNull();
        }

        await using var unconfigured = new RiskWorkerWebApplicationFactory();
        using var plain = Wire(unconfigured, permit: null, out _);
        using var plainScope = plain.Services.CreateScope();
        plainScope.ServiceProvider.GetRequiredService<IShortSellBorrowSource>().Should().BeOfType<UnavailableShortSellBorrowSource>();
    }

    /// <summary>
    /// T-10-1028: 本番のハンドラを通すと、送り手が「許可」と答えた空売りは文脈つきで判定され、ロングが無ければ空売り比率 50% で
    /// ShortExposureExceeded になる（1 銘柄 10% の内側の数量でも）。借株可否は実際に発注執行へ照会している。
    /// </summary>
    [Fact]
    public async Task 本番構成で許可された空売りはロングが無ければ空売り比率50パーセントで拒否される()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        using var wired = Wire(factory, ShortPermitStatus.Permitted, out var stub);

        // 10 × 25 = 250 ≦ 300（10% の内側）。ロング 0 → 250 > (0 + 250) × 0.5。
        var reasons = await RejectionReasonsAsync(wired, ShortEntry(10, 25m));

        reasons.Should().Contain(RejectionReason.ShortExposureExceeded)
            .And.Contain(RejectionReason.BorrowUnavailable, "料率は単位未確定で供給しない（全件拒否は続く）");
        stub.Requests.Should().ContainSingle().Which.Should().Be("/order-execution/short-permit?symbol=MSFT&market=1");
    }

    /// <summary>
    /// T-10-1028: ロング（台帳の約定 ＋ 本番の手元の現在値）があれば空売り比率は満たし、1 銘柄 10%（300）を超える空売りだけが
    /// ShortExposureExceeded になる。内側の数量には立たない（上限の理由が文脈の値に由来することの対照）。
    /// </summary>
    [Fact]
    public async Task 本番構成で許可された空売りは1銘柄10パーセントを超えるときだけ上限で拒否される()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        using var wired = Wire(factory, ShortPermitStatus.Permitted, out _);
        using (var scope = wired.Services.CreateScope())
        {
            // ロング AAPL 20 株（現在値 100 → 時価 2,000）。
            var ledger = scope.ServiceProvider.GetRequiredService<IPortfolioLedgerStore>();
            var id = Guid.NewGuid();
            var at = DateTimeOffset.UtcNow.AddDays(-1);
            ledger.AppendApproval(id, new OrderIntent(
                "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
                20, 100m, PositionEffect.Open, StopLossPrice: 90m), at);
            ledger.AppendFill(id, $"open-{id:N}", 20, 100m, at);
            wired.Services.GetRequiredService<QuoteCache>()
                .Set(new Quote("AAPL", Market.UnitedStates, 100m, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
        }

        // 10 × 40 = 400 > 300。比率は 400 ≦ (2,000 + 400) × 0.5。
        (await RejectionReasonsAsync(wired, ShortEntry(10, 40m)))
            .Should().Contain(RejectionReason.ShortExposureExceeded);
        // 10 × 25 = 250 ≦ 300。比率も内側。
        (await RejectionReasonsAsync(wired, ShortEntry(10, 25m)))
            .Should().NotContain(RejectionReason.ShortExposureExceeded)
            .And.Contain(RejectionReason.BorrowUnavailable);
    }

    /// <summary>
    /// T-10-1029: 送り手が「分からない」と答える（SIMULATE 口座で照会が失敗する、いまの常態）・照会先が無い、のいずれでも
    /// 本番構成は今と同じく BorrowUnavailable で拒否し、10% / 50% は評価しない（偽の文脈を組まない）。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 本番構成で借株可否が分からなければ上限は評価されずBorrowUnavailableで拒否される(bool configured)
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        using var wired = Wire(factory, configured ? ShortPermitStatus.Unknown : null, out var stub);

        var reasons = await RejectionReasonsAsync(wired, ShortEntry(10, 40m));

        reasons.Should().Contain(RejectionReason.BorrowUnavailable)
            .And.NotContain(RejectionReason.ShortExposureExceeded);
        stub.Requests.Should().HaveCount(configured ? 1 : 0);
    }

    // 発注執行の応答（送り手の本物の型を web 既定で直列化）。要求された銘柄・市場をそのまま写す。
    private sealed class PermitStub(ShortPermitStatus status) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var view = new ShortPermitView(
                query["symbol"]!, (Market)int.Parse(query["market"]!, System.Globalization.CultureInfo.InvariantCulture), status,
                status == ShortPermitStatus.Unknown ? ShortPermitUnknownReasons.QueryFailed : null,
                status == ShortPermitStatus.Unknown ? null : DateTimeOffset.UtcNow);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(view, Web), Encoding.UTF8, "application/json"),
            });
        }
    }
}
