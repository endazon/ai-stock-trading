using AiStockTrading.Shared.Contracts.Events;
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
using OrderExecutionService.Infrastructure.Persistence;
using Wolverine;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-831, FR-05, FR-10, #876, IADR-0398: **本番の Program.cs の組み立て**（EF の予約表・スコープ・ハンドラの配線）を
// 通して、見送った承認が OpenD の復帰後に再配送されても発注されないことを固定する。
//
// 単体の試験（OrderExecutionServiceForgoneReplayTests）はインメモリの予約表を自分で渡すため、Program.cs が
// 予約表をどう登録していても通る。ここでは差し替えるのは外界（ブローカー・DB の置き場所・自前の常駐・外部トランスポート）
// だけにし、予約表は Program.cs が登録した実装（EF）をそのまま使う。配送ごとに**別のスコープ**（＝別の DbContext）で
// 処理するので、見送りの記録がコミットされていなければ次の配送からは見えず、本試験が落ちる。
// Program.cs から予約表の登録を外すと、ハンドラの依存が解決できずに本試験は落ちる（登録の抜けも検知する）。
public class ForgoneReplayCompositionTests
{
    // 接続の可否を途中で切り替えられるブローカー。内蔵 paper 構成の Program.cs はアダプタを
    // IOrderAmendmentBroker へも変換して登録するため、それも実装する（訂正は使わない）。
    private sealed class SwitchableBroker : IBrokerAdapter, IProtectiveOrderBroker, IOrderAmendmentBroker
    {
        public bool Available { get; set; }

        public int PlaceCount { get; private set; }

        public BrokerProvider Provider => BrokerProvider.InternalPaper;

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) => Place(intent);

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            Place(closeIntent);

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default) => Place(closeIntent);

        public Task<BrokerOrder> ModifyOrderAsync(
            string orderId, int quantity, decimal price, CancellationToken ct = default) =>
            throw new NotSupportedException("本テストは訂正しない");

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;

        private Task<BrokerOrder> Place(OrderIntent intent)
        {
            if (!Available)
                throw new BrokerUnavailableException("OpenD へ接続できません（テスト）");

            PlaceCount++;
            return Task.FromResult(new BrokerOrder(
                "ORD-" + PlaceCount, intent, OrderStatus.Filled, intent.Quantity, intent.Price,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
    }

    private sealed class ProgramFactory(SwitchableBroker broker) : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Broker:Provider", "paper");
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");

            builder.ConfigureServices(services =>
            {
                // 外界 1: ブローカー（Program.cs の IBrokerAdapter 登録だけを置き換える）。
                services.RemoveAll<IBrokerAdapter>();
                services.AddSingleton<IBrokerAdapter>(broker);

                // 外界 2: DB の置き場所（InMemory）。予約表の実装（EfOrderReservationStore）の登録は Program.cs のまま。
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

                // 外界 3: 自前の常駐（ガード・約定追跡・突合など）。偽のブローカーを巡回で叩かせない。
                var ownHosted = services
                    .Where(d => d.ServiceType == typeof(IHostedService)
                             && d.ImplementationType?.Assembly == typeof(Program).Assembly)
                    .ToList();
                foreach (var d in ownHosted) services.Remove(d);

                services.DisableAllExternalWolverineTransports();
            });
        }
    }

    [Fact]
    public async Task 本番の組み立てでも見送った承認は復帰後の再配送で発注されない()
    {
        var broker = new SwitchableBroker { Available = false };
        await using var factory = new ProgramFactory(broker);
        var intent = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.InternalPaper,
            100, 100m, PositionEffect.Close);
        var approved = new OrderApproved(Guid.NewGuid(), intent, 100, DateTimeOffset.UtcNow);

        // 1 回目の配送（OpenD 再起動中）: 見送り。配送ごとに別のスコープ＝別の DbContext で処理する。
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IOrderReservationStore>().Should()
                .BeOfType<EfOrderReservationStore>("前提: Program.cs が登録した本番の予約表を通っている");
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(approved);
        }

        broker.Available = true; // OpenD が復帰した

        // 2 回目の配送（同じ OrderApproved の重複配送）。
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(approved);
        }

        broker.PlaceCount.Should().Be(0, "見送った承認は、本番の組み立てを通しても再配送で発注しない");
        using var verify = factory.Services.CreateScope();
        verify.ServiceProvider.GetRequiredService<IOrderReservationStore>().Find(approved.DecisionId)!
            .State.Should().Be(OrderDispatchState.Forgone);
        verify.ServiceProvider.GetRequiredService<IExecutedOrderStore>().FindByDecisionId(approved.DecisionId)
            .Should().BeNull("発注していない注文の記録を作らない");
    }
}
