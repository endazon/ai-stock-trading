using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ObserveBrokerPositions;
using OrderExecutionService.Hosted;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-893, FR-10, UC-02, #880, IADR-0412 決定1, IADR-0397: **本番の Program.cs の組み立て**で作った建玉観測の常駐が、
// 帰属不明の建玉の検知を実際に呼ぶことを固定する（結線）。
//
// 単体の試験（BrokerPositionSnapshotUnattributedDetectionTests）は検知の部品を自前のスコープで渡すため、
// Program.cs が検知を登録し忘れても（常駐は巡回ごとにエラーログを出すだけで）緑のまま通る。
// 組み立てガード（CompositionWiringGuardTests）は登録された部品が組めることは見るが、**常駐がそれを呼ぶこと**は見ない。
// ここでは WebApplicationFactory で Program.cs そのものを組み、外界（ブローカー・DB）だけを差し替え、
// 常駐は Program.cs の登録（AddHostedService）どおりの型をコンテナから作って 1 巡回だけ回す（起動はさせない）。
public class UnattributedDetectionCompositionTests
{
    private sealed class ScriptedBroker : IBrokerAdapter, IBrokerPositionSource, IOrderAmendmentBroker
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = [];

        public int PositionQueries { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException("本テストは発注しない");

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task<BrokerOrder> ModifyOrderAsync(
            string orderId, int quantity, decimal price, CancellationToken ct = default) =>
            throw new NotSupportedException("本テストは訂正しない");

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) =>
            throw new NotSupportedException("本テストは取り消さない");

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default)
        {
            PositionQueries++;
            return Task.FromResult(Positions);
        }
    }

    // Program.cs を指定の発注先で組む。外界（ブローカー・DB）を差し替え、自前の常駐は起動させない（登録は控えて外す）。
    private sealed class ProgramFactory(string provider, ScriptedBroker broker) : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();

        public List<ServiceDescriptor> OwnHosted { get; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // 発注先の選択は Program.cs の最上段で読まれるため、ホスト設定（UseSetting）で渡す。
            builder.UseSetting("Broker:Provider", provider);
            builder.UseSetting("Broker:Environment", "sim");
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBrokerAdapter>();
                services.AddSingleton<IBrokerAdapter>(broker);

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

                var ownHosted = services
                    .Where(d => d.ServiceType == typeof(IHostedService)
                             && d.ImplementationType?.Assembly == typeof(Program).Assembly)
                    .ToList();
                foreach (var d in ownHosted)
                {
                    OwnHosted.Add(d);
                    services.Remove(d);
                }

                services.DisableAllExternalWolverineTransports();
            });
        }
    }

    // 稼働 PoC の配置（有効な保護記録 0 件・受理後に 0 約定で取り消された決済を送って完了した S1 行・建玉 10 株）。
    private static ProtectiveStopOrder Seed(IServiceProvider services)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var row = new ProtectiveStopOrder(
            id, ProtectiveStopIds.SoftwareStopId(id), string.Empty, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 338.51m, 1m, Attempt: 1,
            ProtectiveStopState.Completed, now.AddHours(-2), now.AddMinutes(-10), StopLossExecutionMethod.SoftwareStop,
            TriggeredAt: now.AddMinutes(-11), TriggeredPrice: 338m, RemainingProtected: 0);
        services.GetRequiredService<IProtectiveStopOrderStore>().Save(row);
        var store = services.GetRequiredService<IExecutedOrderStore>();
        store.Save(new ExecutionRecord(
            id, "entry-1", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, PositionEffect.Open,
            10, 340m, 10, 340m, OrderStatus.Filled, 0m, now.AddHours(-2)));
        store.Save(new ExecutionRecord(
            ProtectiveStopIds.SoftwareCloseDecisionId(id, 1), "close-1", "AAPL", Market.UnitedStates, TradeSide.Sell,
            ProductType.Cash, PositionEffect.Close, 10, 338m, 0, 0m, OrderStatus.Cancelled, 0m, now.AddMinutes(-10)));
        return row;
    }

    [Fact]
    public async Task T_10_893_moomoo構成ではProgramの常駐が建玉観測の巡回で帰属不明を検知する()
    {
        // 殺す変異: ①常駐から検知の呼び出しを外す ②Program.cs から検知の登録を外す（常駐はエラーログだけで緑のまま）。
        var broker = new ScriptedBroker { Positions = [new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 10, 340m)] };
        await using var factory = new ProgramFactory("moomoo", broker);
        _ = factory.Services; // ホストを組む（自前の常駐は外してある）。

        var descriptor = factory.OwnHosted.Should()
            .ContainSingle(d => d.ImplementationType == typeof(BrokerPositionSnapshotService),
                "前提: moomoo 構成の Program.cs は建玉観測の常駐を登録する")
            .Subject;
        ProtectiveStopOrder row;
        using (var seed = factory.Services.CreateScope())
            row = Seed(seed.ServiceProvider);

        var service = (BrokerPositionSnapshotService)ActivatorUtilities.CreateInstance(
            factory.Services, descriptor.ImplementationType!);
        var published = await service.PublishOnceAsync(CancellationToken.None);

        published.Should().BeTrue();
        broker.PositionQueries.Should().Be(1, "検知は観測のスナップショットに相乗りし、照会を増やさない");
        using var verify = factory.Services.CreateScope();
        verify.ServiceProvider.GetRequiredService<IProtectiveStopOrderStore>().Find(row.EntryDecisionId)!
            .UnattributedNotifiedQuantity.Should().Be(10, "Program.cs の組み立てで検知が走り、通知済みの印を書いた");
    }

    [Fact]
    public async Task T_10_893_paper構成では建玉観測の常駐も検知も登録されない()
    {
        // 対の肯定形: 内蔵 paper は建玉照会を持たないので、検知だけが単独で動く経路も無い（構造的な非干渉）。
        var broker = new ScriptedBroker();
        await using var factory = new ProgramFactory("paper", broker);
        using var scope = factory.Services.CreateScope();

        factory.OwnHosted.Should().NotContain(d => d.ImplementationType == typeof(BrokerPositionSnapshotService));
        scope.ServiceProvider.GetService<UnattributedPositionDetector>().Should().BeNull();
        broker.PositionQueries.Should().Be(0);
    }
}
