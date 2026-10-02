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

// 🔴 FR-10, FR-11, #1048（利用者裁定 2026-10-02・Q3）, IADR-0481 決定3: 約定追跡の打ち切りを監査台帳へ記録する。
// 免除（S2）の記録と同じ相関（エントリーの DecisionId）に並び、免除が発注数量のまま残る状態を追跡できる。
// 発注執行は「発行の後に印を書く」ため同じ打ち切りを再発行し得るが、台帳には 1 件だけ残る。
// 作業仕様書 20261002_1048_same-symbol-method-coexistence-and-fill-tracking §受け入れ基準 15〜16。
public class OrderFillTrackingAbandonedAuditTests
{
    private static readonly DateTimeOffset TrackedFrom = new(2026, 10, 1, 5, 0, 0, TimeSpan.Zero);

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

    private static OrderFillTrackingAbandoned Abandoned(Guid decisionId, DateTimeOffset trackedFrom) =>
        new(decisionId, "ORD-1", "AAPL", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 10, 0,
            OrderStatus.Accepted, trackedFrom, TimeSpan.FromHours(24), BrokerProvider.MoomooSimulate, trackedFrom.AddHours(24));

    // T-10-2119: 受け入れ基準 15。免除と同じ相関に、打ち切りの記録が「以後、終端の約定記録が届かない」「免除は発注数量のまま」と読める形で残る。
    // 再発行（同じ注文・同じ追跡の起点）でも 1 件。追跡の起点が違えば別の打ち切りとして 2 件目が残る。
    [Fact]
    public async Task T_10_2119_打ち切りは免除と同じ相関に1件だけ残り起点が違えば別に残る()
    {
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);
        var decisionId = Guid.NewGuid();

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new ProtectiveStopWaived(
            decisionId, "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, 10, 950m,
            StopLossExecutionMethod.NoProtectiveStop, BrokerProvider.MoomooSimulate, TrackedFrom));
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Abandoned(decisionId, TrackedFrom));
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Abandoned(decisionId, TrackedFrom));

        var chain = store.GetByCorrelation(decisionId);
        var abandoned = chain.Where(e => e.EventType == nameof(OrderFillTrackingAbandoned)).ToList();
        abandoned.Should().ContainSingle("同じ打ち切りの再発行は 1 件に畳む")
            .Which.Summary.Should().Contain("約定追跡を打ち切り").And.Contain("終端の約定記録は台帳へ届かない")
            .And.Contain("発注数量のまま").And.Contain("ORD-1");
        chain.Should().Contain(e => e.EventType == nameof(ProtectiveStopWaived), "免除と同じ相関で辿れる");
        chain.Should().NotContain(e => e.EventType == ProtectiveStopWaiverSettlement.EventType, "打ち切りは免除を打ち消さない（終端ではない）");

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Abandoned(decisionId, TrackedFrom.AddDays(1)));
        store.GetByCorrelation(decisionId).Count(e => e.EventType == nameof(OrderFillTrackingAbandoned))
            .Should().Be(2, "追跡の起点が違えば別の打ち切り");

        await host.StopAsync();
    }

    // T-10-2120: 受け入れ基準 16。記録 Id は注文 ID と追跡の起点だけから決まる（他の値・受信の Id に依らない）。
    [Fact]
    public void T_10_2120_記録Idは注文と追跡の起点から決定的に導く()
    {
        AuditEntryFactory.FillTrackingAbandonedIdFor("ORD-1", TrackedFrom)
            .Should().Be(AuditEntryFactory.FillTrackingAbandonedIdFor("ORD-1", TrackedFrom.ToOffset(TimeSpan.FromHours(9))),
                "同じ時刻なら表記の時差に依らない");
        AuditEntryFactory.FillTrackingAbandonedIdFor("ORD-1", TrackedFrom)
            .Should().NotBe(AuditEntryFactory.FillTrackingAbandonedIdFor("ORD-2", TrackedFrom));
        AuditEntryFactory.FillTrackingAbandonedIdFor("ORD-1", TrackedFrom)
            .Should().NotBe(AuditEntryFactory.FillTrackingAbandonedIdFor("ORD-1", TrackedFrom.AddTicks(1)));
    }
}
