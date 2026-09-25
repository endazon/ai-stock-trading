using AuditService.Common.Abstractions;
using AuditService.Domain;
using AuditService.Features.AuditEvents;
using AuditService.Infrastructure.Persistence;
using AuditService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Xunit;

namespace AuditService.Tests;

// FR-10, FR-11, FR-12, ADR-0040 決定1（S2）, #826 項目 5, IADR-0413 決定2: 本番と同じ監査ハンドラの配線で、
// 免除（受付時点）と終端の約定記録が揃ったら、到着順に依らず打ち消しの記録が 1 件だけ残る。
public class ProtectiveStopWaiverSettlementConsumersTests
{
    private static Task<IHost> BuildHostAsync(InMemoryAuditEventStore store) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, SystemClock>();
                opts.Services.AddSingleton<IAuditEventStore>(store);
                opts.Services.AddSingleton<BusinessMetrics>();
                // 本番（Program.cs）と同じ発見範囲。
                opts.Discovery.IncludeAssembly(typeof(PriceMovementDetectedAuditHandler).Assembly);
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static ProtectiveStopWaived Waived(Guid decisionId) =>
        new(decisionId, "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, 10, 950m,
            StopLossExecutionMethod.NoProtectiveStop, BrokerProvider.MoomooSimulate, DateTimeOffset.UtcNow);

    private static OrderExecuted Executed(Guid decisionId, OrderStatus status) =>
        new(decisionId, "ORD-1", status, 0, 0m, DateTimeOffset.UtcNow, BrokerProvider.MoomooSimulate);

    private static IReadOnlyList<AuditEntry> Settled(InMemoryAuditEventStore store, Guid decisionId) =>
        [.. store.GetByCorrelation(decisionId).Where(e => e.EventType == ProtectiveStopWaiverSettlement.EventType)];

    // T-10-901: 発注執行が出す順（免除 → 受付 → 取消〔約定 0〕）で流すと、打ち消しが 1 件残る。
    [Fact]
    public async Task 免除のあと約定0で取消されたら打ち消しが台帳に残る()
    {
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);
        var decisionId = Guid.NewGuid();

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Waived(decisionId));
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Executed(decisionId, OrderStatus.Accepted));
        Settled(store, decisionId).Should().BeEmpty("受付（非終端）では確定しない");

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Executed(decisionId, OrderStatus.Cancelled));

        Settled(store, decisionId).Should().ContainSingle()
            .Which.Summary.Should().Contain("免除を打ち消し").And.Contain("建玉は生じなかった");

        await host.StopAsync();
    }

    // T-10-900: 到着順が逆（終端の約定記録が先・免除が後）でも 1 件残り、両メッセージの再配送でも 1 件のまま。
    [Fact]
    public async Task 到着順が逆でも再配送でも打ち消しは1件だけ残る()
    {
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);
        var decisionId = Guid.NewGuid();

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Executed(decisionId, OrderStatus.Expired));
        Settled(store, decisionId).Should().BeEmpty("免除がまだ届いていない");

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Waived(decisionId));
        Settled(store, decisionId).Should().ContainSingle("免除の側からも揃った時点で残す");

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Executed(decisionId, OrderStatus.Expired));
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Waived(decisionId));

        Settled(store, decisionId).Should().ContainSingle("Id は相関から決定的に導くため両経路・再配送で畳まれる")
            .Which.Id.Should().Be(ProtectiveStopWaiverSettlement.IdFor(decisionId));

        await host.StopAsync();
    }
}
