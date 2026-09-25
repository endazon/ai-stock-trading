using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;
using OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;
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
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Xunit;
using Behavior = OrderExecutionService.Tests.StopLegScriptedBroker.StopBehavior;

namespace OrderExecutionService.Tests;

// 🔴 T-10-1073, FR-10, FR-12, #853, IADR-0428 決定4: **本番の Program.cs の組み立て**で、突合（OrderReservationReconciler）が
// 発注執行（OrderExecutionAppService）の保護の口を使い、確定したエントリーに逆指値を張ることを固定する。
//
// 保護の口（IReconciledEntryProtection）はリコンサイラの省略可能な引数である。Program.cs が渡し忘れても、コンパイルも
// 単体の試験（自分で new する）も通ったまま、突合で確定したエントリーは保護レグを持たないまま台帳へ載る（PR #919 / #918 と同じ形）。
// ここでは Program.cs そのものを組み、差し替えるのは外界（ブローカー・照会プローブ・DB・常駐・外部トランスポート）だけにする。
//
// 殺す変異: Program.cs の IReconciledEntryProtection の登録を外す／リコンサイラへ渡す引数を落とす（どちらも逆指値が 0 本で赤）。
public class ReconciledEntryProtectionCompositionTests
{
    private static readonly DateTimeOffset StalledAt = DateTimeOffset.UtcNow.AddHours(-30);

    private sealed class PlacedProbe : IReservationBrokerProbe
    {
        public Task<ReservationProbeResult> ProbeAsync(
            OrderDispatchReservation reservation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ReservationProbeResult.Placed(new BrokerOrder(
                "entry-found",
                new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
                    10, 1_000m, PositionEffect.Open),
                OrderStatus.Filled, 10, 1_000m, PlacedAt: StalledAt, CompletedAt: StalledAt)));
    }

    // Program.cs を moomoo 構成で組む。外界だけを差し替える（ForgoneCloseProtectionCompositionTests と同じ作法）。
    private sealed class ProgramFactory(StopLegScriptedBroker broker) : WebApplicationFactory<Program>
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
                services.RemoveAll<IReservationBrokerProbe>();
                services.AddSingleton<IReservationBrokerProbe>(new PlacedProbe());

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

                services.DisableAllExternalWolverineTransports();
            });
        }
    }

    private static Guid SeedAwaitingEntry(IServiceProvider services)
    {
        var entry = Guid.NewGuid();
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IOrderReservationStore>().TryReserve(entry, StalledAt);
        scope.ServiceProvider.GetRequiredService<IProtectiveStopOrderStore>().Save(new ProtectiveStopOrder(
            entry, ProtectiveStopIds.StopDecisionId(entry, 1), string.Empty, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, 0, ProtectiveStopState.AwaitingEntry,
            StalledAt, StalledAt));
        return entry;
    }

    [Fact]
    public async Task Programの突合は発注執行の保護の口で確定したエントリーに逆指値を張る()
    {
        var broker = new StopLegScriptedBroker { Stop = Behavior.Accept };
        await using var factory = new ProgramFactory(broker);
        var entry = SeedAwaitingEntry(factory.Services);

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IReconciledEntryProtection>().Should().BeSameAs(
            scope.ServiceProvider.GetRequiredService<OrderExecutionAppService>(),
            "保護の口の実体は発注執行そのもの（平常の経路と同じ PlaceProtectiveStopAsync を通す）");
        var reconciler = scope.ServiceProvider.GetRequiredService<OrderReservationReconciler>();

        var result = await reconciler.ReconcileAsync(DateTimeOffset.UtcNow.AddHours(-2), 50);

        result.Protections.Should().ContainSingle().Which.Outcome!.Kind
            .Should().Be(ReconciledEntryProtectionKind.BrokerStopPlaced);
        broker.StopPlaceCount.Should().Be(1, "Program.cs のリコンサイラが保護の口を持っていれば、確定したエントリーに逆指値が張られる");
        using var check = factory.Services.CreateScope();
        var row = check.ServiceProvider.GetRequiredService<IProtectiveStopOrderStore>().Find(entry)!;
        row.State.Should().Be(ProtectiveStopState.Active);
        row.StopOrderId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Programの突合で逆指値が届いたか不明なら送信結果待ちを本番のストアへ残し取消も成行もしない()
    {
        // 本番のストア（EF）を通しても、送信結果待ち（Active・注文 ID が空）と逆指値レグの予約（Reserved）が残る
        // ＝常駐ガードがこの建玉を巡回し、送り直さずに突合の結果を待つ。
        var broker = new StopLegScriptedBroker { Stop = Behavior.Indeterminate };
        await using var factory = new ProgramFactory(broker);
        var entry = SeedAwaitingEntry(factory.Services);

        using (var scope = factory.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<OrderReservationReconciler>()
                .ReconcileAsync(DateTimeOffset.UtcNow.AddHours(-2), 50);
            result.Protections.Should().ContainSingle().Which.Outcome!.Kind
                .Should().Be(ReconciledEntryProtectionKind.StopDispatchHeld);
        }

        using var check = factory.Services.CreateScope();
        var row = check.ServiceProvider.GetRequiredService<IProtectiveStopOrderStore>().Find(entry)!;
        row.State.Should().Be(ProtectiveStopState.Active);
        row.IsStopDispatchPending.Should().BeTrue();
        check.ServiceProvider.GetRequiredService<IOrderReservationStore>()
            .Find(ProtectiveStopIds.StopDecisionId(entry, 1))!.State.Should().Be(OrderDispatchState.Reserved);
        broker.MarketCloseCount.Should().Be(0);
        broker.CancelCount.Should().Be(0);
    }
}
