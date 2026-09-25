using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;
using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using JasperFx.CodeGeneration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.RabbitMQ;
using Wolverine.RabbitMQ.Internal;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-1008・T-10-1011（受け手側）, FR-10, FR-09, UC-06, #879, IADR-0424 決定3: **本番の Program.cs の組み立て**で固定する。
//
// T-10-1008: 見送りに載せる保護の記録は、Program.cs が OrderExecutionAppService へ記録ストア（IProtectiveStopOrderStore）を渡している
// ときにしか判別できない。引数は省略可能なので、Program.cs が渡し忘れてもコンパイルも単体の試験（自分で new する）も通り、
// 通知は常に「保護の記録を確認できませんでした（可能性）」と書く——断定できる建玉まで「可能性」に落ちる（PR #919 / #918 と同じ形）。
// ここでは Program.cs そのものを組み、差し替えるのは外界（ブローカー・DB・常駐・外部トランスポート）だけにする。
//
// T-10-1011（受け手側）: 取り込みイベント（PositionDriftAdopted）を Program.cs の Wolverine が PositionDriftAdoptedHandler で処理し、
// 規約のキュー（ai-stock-trading.order-execution-service.PositionDriftAdopted）で購読していること。発注執行が取り込みを
// 「購読していない」と書いた記述（#879 の起票時）が現物と食い違っていたため、購読の事実を本番の組み立てで固定する。
public class ForgoneCloseProtectionCompositionTests
{
    // 建玉照会は「不明」（null）を返す。発注されたら試験を落とす。内蔵 paper 構成の Program.cs が要求する訂正の能力も持つ。
    internal sealed class IndeterminateBroker(IReadOnlyList<BrokerPositionSnapshot>? positions = null)
        : IBrokerAdapter, IBrokerPositionSource, IOrderAmendmentBroker
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new InvalidOperationException("照会不明の決済は送らない（IADR-0355 決定3）");

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task<BrokerOrder> ModifyOrderAsync(
            string orderId, int quantity, decimal price, CancellationToken ct = default) =>
            throw new NotSupportedException("本テストは訂正しない");

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult(positions);
    }

    // Program.cs を moomoo 構成で組む。外界だけを差し替える（ProtectiveStopDriftAdopterCompositionTests と同じ作法）。
    // stubTransports: 外部トランスポートを外すのではなくスタブにする（購読キューの宣言を観測するため。接続はしない）。
    internal sealed class ProgramFactory(IndeterminateBroker broker, bool stubTransports = false) : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // 🔴 発注先の選択は Program.cs の最上段（builder.Build() の前）で読まれるため、ホスト設定で渡す。
            builder.UseSetting("Broker:Provider", "moomoo");
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
                foreach (var d in ownHosted) services.Remove(d);

                if (stubTransports)
                    services.ConfigureWolverine(opts => opts.StubAllExternalTransports());
                else
                    services.DisableAllExternalWolverineTransports();
            });
        }
    }

    private static OrderApproved CloseApproval(int qty = 300) =>
        new(Guid.NewGuid(),
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
                qty, 100m, PositionEffect.Close),
            qty, DateTimeOffset.UtcNow);

    // ---- T-10-1008: Program.cs が記録ストアを渡し、判別できる建玉は断定の分類になる ----
    // 🔴 殺す変異: Program.cs で OrderExecutionAppService へ記録ストアの代わりに null を渡す（どちらの試験も Unknown になって赤）。
    [Fact]
    public async Task Programの発注サービスは照会不明の見送りに保護記録なしを載せる()
    {
        var broker = new IndeterminateBroker();
        await using var factory = new ProgramFactory(broker);
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetService<IBrokerPositionSource>().Should()
            .BeSameAs(broker, "前提: moomoo の分岐を通り、建玉照会はアダプタから作られている（見送りの門が有効）");
        var service = scope.ServiceProvider.GetRequiredService<OrderExecutionAppService>();

        var result = await service.ExecuteAsync(CloseApproval());

        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.BrokerPositionsIndeterminate);
        result.Forgone.Protection.Should().Be(
            new ForgoneCloseProtection(ForgoneCloseProtectionStatus.NoneRecorded, 0, 0),
            "Program.cs が記録ストアを渡していれば「無い」と断定できる（渡し忘れは Unknown）");
    }

    [Fact]
    public async Task Programの発注サービスは照会不明の見送りに保護記録の株数を載せる()
    {
        var broker = new IndeterminateBroker();
        await using var factory = new ProgramFactory(broker);
        using var scope = factory.Services.CreateScope();
        var now = DateTimeOffset.UtcNow;
        var entry = Guid.NewGuid();
        scope.ServiceProvider.GetRequiredService<IProtectiveStopOrderStore>().Save(new ProtectiveStopOrder(
            entry, ProtectiveStopIds.StopDecisionId(entry, 1), "stop-1", "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 120, 95m, 1m, 1, ProtectiveStopState.Active,
            now.AddHours(-1), now.AddHours(-1), RemainingProtected: 120));
        var service = scope.ServiceProvider.GetRequiredService<OrderExecutionAppService>();

        var result = await service.ExecuteAsync(CloseApproval());

        result.Forgone!.Protection.Should().Be(
            new ForgoneCloseProtection(ForgoneCloseProtectionStatus.Recorded, 120, 0));
    }

    // ---- T-10-1011（受け手側）: Program.cs の Wolverine が取り込みを購読し、PositionDriftAdoptedHandler で処理する ----
    // 🔴 殺す変異: ハンドラの削除・改名で Handle が規約から外れる／UseAiStockTradingRabbitMq の呼び出しを外す（購読のキューが消える）。
    [Fact]
    public async Task Programは取り込みイベントをPositionDriftAdoptedHandlerで処理し規約のキューで購読する()
    {
        var broker = new IndeterminateBroker();
        await using var factory = new ProgramFactory(broker, stubTransports: true);
        _ = factory.Services; // ホストを起動する（Wolverine のハンドラ探索と購読の宣言を走らせる）

        var chains = factory.Services.GetServices<ICodeFileCollection>().OfType<HandlerGraph>()
            .SelectMany(g => g.AllChains())
            .Where(c => c.MessageType == typeof(PositionDriftAdopted))
            .ToList();
        chains.Should().ContainSingle("発注執行は取り込みイベントのハンドラを 1 つだけ持つ")
            .Which.Handlers.Select(h => h.HandlerType).Should().Equal(typeof(PositionDriftAdoptedHandler));

        // 購読の宣言: RabbitMQ の規約経路（UseAiStockTradingRabbitMq）は、**ハンドラを持つ型ごとに**型名の共有 fanout exchange を宣言し、
        // そこへサービス名つきの購読キュー（QueueNameFor）を束縛する（IADR-0129 決定1・2）。外部トランスポートはスタブなので接続も
        // キューの実体化もしないが、exchange の宣言は起動時に本番と同じく行われる（実測: 宣言された exchange はこのサービスが
        // ハンドラを持つ 4 型ちょうどで、発行するだけの型〔OrderExecuted 等〕は含まれない）。送り手の発行先がこの exchange と
        // 同じ名前であることは、リスク管理側の T-10-1011（送り手側）が同じ名前で固定する。
        var runtime = factory.Services.GetRequiredService<IWolverineRuntime>();
        var rabbit = runtime.Options.Transports.GetOrCreate<RabbitMqTransport>();
        var exchange = rabbit.Exchanges.SingleOrDefault(x => x.ExchangeName == typeof(PositionDriftAdopted).FullName);
        exchange.Should().NotBeNull("Program.cs の規約経路が、ハンドラを持つ取り込みイベントの共有 exchange を宣言している");
        exchange!.ExchangeType.Should().Be(ExchangeType.Fanout, "購読するサービスごとのキューへ配る（IADR-0129 決定2）");
        rabbit.Exchanges.Select(x => x.ExchangeName).Should().NotContain(
            typeof(OrderExecuted).FullName, "前提: 宣言はハンドラを持つ型に限られる（発行だけの型は起動時に宣言されない）");
        WolverineExtensions.QueueNameFor(runtime.Options.ServiceName, typeof(PositionDriftAdopted)).Should().Be(
            "ai-stock-trading.order-execution-service.PositionDriftAdopted",
            "購読キューの名前は Program.cs のサービス名から導かれる（IADR-0129 決定1。運用手順の _error キュー名と一致する）");
    }

    // 🔴 T-10-1011（受け手側・続き）: 宣言だけでなく、Program.cs のホストのバスへ取り込みを流すと本番のハンドラが業務クラスを呼ぶ。
    // 減らす取り込み（10→0）で、帳簿だけの行（S1）の主張が 0 になり完了する（建玉照会は「0 株」を返す＝確かめられた構成）。
    [Fact]
    public async Task Programのバスへ流した減らす取り込みは本番のハンドラが保護記録を追随させる()
    {
        var broker = new IndeterminateBroker(positions: []);
        await using var factory = new ProgramFactory(broker);
        var now = DateTimeOffset.UtcNow;
        var entry = Guid.NewGuid();
        using (var seed = factory.Services.CreateScope())
        {
            seed.ServiceProvider.GetRequiredService<IProtectiveStopOrderStore>().Save(new ProtectiveStopOrder(
                entry, Guid.NewGuid(), string.Empty, "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
                BrokerProvider.MoomooSimulate, 10, 95m, 1m, 0, ProtectiveStopState.Active, now.AddHours(-1), now.AddHours(-1),
                Mechanism: StopLossExecutionMethod.SoftwareStop, RemainingProtected: 10));
        }

        await factory.Services.GetRequiredService<IMessageBus>().InvokeAsync(new PositionDriftAdopted(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, 10, 0, 0, now.AddMinutes(-5), 100m,
            RealizedPnlRecorded: false, ReferencePrice: null, EstimatedPnlInBase: null,
            Actor: "owner", Reason: "証券会社のアプリで全株売却", AdoptedAt: now));

        using var check = factory.Services.CreateScope();
        var row = check.ServiceProvider.GetRequiredService<IProtectiveStopOrderStore>().Find(entry)!;
        row.RemainingProtected.Should().Be(0, "本番のハンドラが業務クラスを呼び、取り込みで消えた建玉の主張を減らした");
        row.State.Should().Be(ProtectiveStopState.Completed);
    }
}
