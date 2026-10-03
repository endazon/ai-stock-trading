using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.QueryShortPermit;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Infrastructure.ExternalServices.RealReadOnly;
using OrderExecutionService.Infrastructure.Persistence;
using Wolverine;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 FR-10, FR-05, NFR-09, #856, IADR-0488 決定2・6（T-10-2230 / T-10-2236 / T-10-2239）: 本番の組み立て（Program.cs）での故障注入。
//   - 既定（未設定）は包まない: 承認の配送の口（アダプタ）は素のクライアントで送る。
//   - 有効: アダプタへ渡すクライアントだけが包まれ、DI の IMoomooTradeClient・借株可否の照会・突合のプローブは素のまま。
//   - 内蔵 paper・live 階層・実弾口座の照会と同居なら起動時に止める。
public class IndeterminateDispatchFaultInjectionCompositionTests
{
    private static OrderIntent EntryIntent(string symbol = "AAPL") =>
        new(symbol, Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
            Quantity: 1, Price: 100m, PositionEffect.Open, StopLossPrice: 95m);

    private static Dictionary<string, string?> Injection(string mode, string symbols = "AAPL") => new()
    {
        [IndeterminateDispatchFaultInjectionOptions.ModeKey] = mode,
        [IndeterminateDispatchFaultInjectionOptions.SymbolsKey] = symbols,
        [IndeterminateDispatchFaultInjectionOptions.ExpiresAtKey] = DateTimeOffset.UtcNow.AddHours(2).ToString("O"),
    };

    // T-10-2230: 既定は包まない。承認の配送の口は素のクライアントで送り、注入の型はどこにも現れない。
    [Fact]
    public async Task T_10_2230_既定ではアダプタのクライアントを包まない()
    {
        await using var factory = new MoomooFactory(new Dictionary<string, string?>());

        var adapter = (IClientOrderIdBroker)factory.Services.GetRequiredService<IBrokerAdapter>();
        var order = await adapter.PlaceOrderAsync(EntryIntent(), Guid.NewGuid(), TestContext.Current.CancellationToken);

        order.OrderId.Should().Be("1");
        factory.TradeClient.Placed.Should().ContainSingle();
    }

    // 🔴 T-10-2239: BeforeSend を有効にすると、承認の配送の口は送信せずに「届いたか不明」になる。
    // DI の IMoomooTradeClient・借株可否の照会・突合のプローブは素のクライアントのまま。
    [Fact]
    public async Task T_10_2239_有効にするとアダプタの口にだけ注入が効く()
    {
        await using var factory = new MoomooFactory(Injection("BeforeSend"));
        var services = factory.Services;

        var adapter = (IClientOrderIdBroker)services.GetRequiredService<IBrokerAdapter>();
        var act = async () => await adapter.PlaceOrderAsync(EntryIntent(), Guid.NewGuid(), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<BrokerDispatchIndeterminateException>())
            .Which.InnerException.Should().BeOfType<IndeterminateDispatchFaultInjectedException>();
        factory.TradeClient.Placed.Should().BeEmpty("BeforeSend は証券会社へ送らない");

        services.GetRequiredService<IMoomooTradeClient>().Should().BeSameAs(factory.TradeClient, "DI のクライアントは包まない");
        services.GetRequiredService<IShortPermitSource>().Should().BeSameAs(factory.TradeClient, "借株可否の照会は型変換で取り出す");
        var view = await services.GetRequiredService<ShortPermitQueryService>()
            .QueryAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken);
        view.Status.Should().Be(ShortPermitStatus.Permitted);
    }

    // T-10-2239: AfterSend を有効にすると、承認の配送の口は送信した後で「届いたか不明」になる。1 回だけ。
    [Fact]
    public async Task T_10_2239_AfterSendは送信した後で届いたか不明になり2本目は素通し()
    {
        await using var factory = new MoomooFactory(Injection("AfterSend", "*"));
        var adapter = (IClientOrderIdBroker)factory.Services.GetRequiredService<IBrokerAdapter>();
        var ct = TestContext.Current.CancellationToken;

        var act = async () => await adapter.PlaceOrderAsync(EntryIntent("MSFT"), Guid.NewGuid(), ct);
        await act.Should().ThrowAsync<BrokerDispatchIndeterminateException>();
        var second = await adapter.PlaceOrderAsync(EntryIntent("MSFT"), Guid.NewGuid(), ct);

        factory.TradeClient.Placed.Should().HaveCount(2);
        second.OrderId.Should().Be("2");
    }

    // 🔴 T-10-2236: 内蔵 paper で有効にしたら起動時に止める（包む先が無い＝「有効のつもり」を作らない）。
    [Fact]
    public void T_10_2236_内蔵paperで有効にすると起動時に止める()
    {
        using var factory = new MoomooFactory(Injection("BeforeSend"), provider: "paper");

        var act = () => factory.Services;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{IndeterminateDispatchFaultInjectionOptions.ModeKey}*SIMULATE 限定*");
    }

    // 🔴 T-10-2236: live 階層は閂（LiveTradingGate）が先に止める＝閂は変わらない。
    [Fact]
    public void T_10_2236_実弾階層は閂が止める()
    {
        using var factory = new MoomooFactory(Injection("BeforeSend"), environment: "live");

        var act = () => factory.Services;

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("moomoo-live");
    }

    // 🔴 T-10-2236: 実弾口座の読み取り専用の照会と同じプロセスでは起動時に止める。
    [Fact]
    public void T_10_2236_実弾口座の照会が有効なら起動時に止める()
    {
        var settings = Injection("AfterSend");
        settings[RealMarginQueryOptions.EnabledKey] = "true";
        using var factory = new MoomooFactory(settings);

        var act = () => factory.Services;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{IndeterminateDispatchFaultInjectionOptions.ModeKey}*実弾口座の読み取り専用の照会*");
    }

    // T-10-2237: 不正な値は起動時に止める（本番の組み立てでも）。
    [Fact]
    public void T_10_2237_不正な形は起動時に止める()
    {
        using var factory = new MoomooFactory(Injection("Sometimes"));

        var act = () => factory.Services;

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{IndeterminateDispatchFaultInjectionOptions.ModeKey}*");
    }

    // 送った注文を数える OpenD の代わり。借株可否の照会の口も持つ（Program.cs は DI のクライアントをこの型へ変換する）。
    internal sealed class CountingOpenD : IMoomooTradeClient, IShortPermitSource
    {
        private readonly List<MoomooOrderRequest> _placed = [];

        public IReadOnlyList<MoomooOrderRequest> Placed
        {
            get { lock (_placed) return _placed.ToArray(); }
        }

        public Task<bool?> GetShortPermitAsync(string symbol, Market market, CancellationToken cancellationToken = default) =>
            Task.FromResult<bool?>(true);

        public Task<MoomooOrderResult> PlaceOrderAsync(MoomooOrderRequest request, CancellationToken cancellationToken = default)
        {
            int count;
            lock (_placed)
            {
                _placed.Add(request);
                count = _placed.Count;
            }
            return Task.FromResult(new MoomooOrderResult(
                count.ToString(System.Globalization.CultureInfo.InvariantCulture), MoomooOrderState.Submitted, 0, 0m));
        }

        public Task<MoomooOrderResult?> QueryOrderAsync(string orderId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MoomooOrderResult?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<MoomooOrderSnapshot?> FindOrderByClientIdAsync(
            string clientOrderId, DateTimeOffset reservedAtUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<MoomooOrderSnapshot?>(null);

        public Task<IReadOnlyList<MoomooPositionSnapshot>> GetPositionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MoomooPositionSnapshot>>([]);

        public Task<MoomooAccountType?> GetAccountTypeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<MoomooAccountType?>(MoomooAccountType.Margin);

        public Task<decimal?> GetAccountEquityInBaseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<decimal?>(3_000m);
    }

    // moomoo（SIMULATE）構成。差し替えるのは OpenD・DB・外部トランスポート・常駐だけ。
    private sealed class MoomooFactory(
        IReadOnlyDictionary<string, string?> settings, string provider = "moomoo", string environment = "sim")
        : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();

        public CountingOpenD TradeClient { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Broker:Provider", provider);
            builder.UseSetting("Broker:Environment", environment);
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMoomooTradeClient>();
                services.AddSingleton<IMoomooTradeClient>(TradeClient);

                foreach (var hosted in services
                             .Where(d => d.ServiceType == typeof(IHostedService)
                                      && d.ImplementationType?.Namespace == "OrderExecutionService.Hosted")
                             .ToList())
                {
                    services.Remove(hosted);
                }

                var toRemove = services
                    .Where(d => d.ServiceType == typeof(DbContextOptions<OrderExecutionDbContext>)
                             || (d.ServiceType.IsGenericType
                                 && d.ServiceType.GetGenericTypeDefinition().FullName?
                                     .Contains("IDbContextOptionsConfiguration") == true
                                 && d.ServiceType.GenericTypeArguments.Length == 1
                                 && d.ServiceType.GenericTypeArguments[0] == typeof(OrderExecutionDbContext)))
                    .ToList();
                foreach (var d in toRemove) services.Remove(d);
                services.AddDbContext<OrderExecutionDbContext>(opt => opt.UseInMemoryDatabase(_dbName));

                services.DisableAllExternalWolverineTransports();
            });
        }
    }
}
