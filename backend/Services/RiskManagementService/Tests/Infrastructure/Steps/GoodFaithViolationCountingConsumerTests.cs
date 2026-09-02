using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Common.Abstractions;
using RiskManagementService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Tests;

// FR-19, FR-10, FR-11, UC-06, #425, ADR-0025 決定2, IADR-0165:
// **GFV 自前計数の供給経路**（約定 → 事後検出 → 追記台帳 → 監査イベントの発行）の検証。
//
// 判定そのものは純関数（GoodFaithViolationDetection・Domain.Tests）で固定してある。ここで確かめるのは
//   1. 記録が**監査証跡（FR-11）へ運ばれること**——ADR-0025 が手入力を採らなかった理由の 1 つが
//      「監査証跡に乗らない」ことであった
//   2. **記録されない事象で監査を汚さないこと**（否定形）
//
// ★ 記録しているのは「自らのガードをすり抜けた買付」であり、**ブローカーが GFV と判定した件数ではない**。
public class GoodFaithViolationCountingConsumerTests
{
    private const string ServiceName = "ai-stock-trading.risk-management-service";

    // 米国東部時間 2026-08-07 09:30（EDT = UTC-4）。取引日は 2026-08-07 である。
    private static readonly DateTimeOffset ExecutedAt = new(2026, 8, 7, 13, 30, 0, TimeSpan.Zero);
    private static readonly Guid DecisionId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;

        public DateOnly Today => DateOnly.FromDateTime(utcNow.UtcDateTime);
    }

    private sealed class StubAccountObservations(BrokerAccountState? account) : IBrokerAccountObservationStore
    {
        public void Record(BrokerAccountState account_, DateTimeOffset observedAt) { }

        public BrokerAccountState? GetCurrent() => account;
    }

    private static Task<IHost> BuildHostAsync(
        IGoodFaithViolationStore store, BrokerAccountState? account, OrderIntent? approved)
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        if (approved is not null)
        {
            ledger.AppendApproval(DecisionId, approved, ExecutedAt.AddMinutes(-1));
        }

        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(store);
                opts.Services.AddSingleton(new GoodFaithViolationCountingService(
                    ledger, new StubAccountObservations(account), store, new FixedClock(ExecutedAt)));
                // ルーティングを入れないと発行先が 1 つも無く、送信そのものが起きない
                // （BrokerPositionsObservedConsumerTests と同じ理由）。
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<OrderExecutedGoodFaithViolationHandler>();
                opts.StubAllExternalTransports();
            })
            .StartAsync();
    }

    private static OrderExecuted Fill(string orderId = "ORD-1") =>
        new(DecisionId, orderId, OrderStatus.Filled, 1, 1_000m, ExecutedAt, BrokerProvider.MoomooSimulate);

    private static OrderIntent BuyEntry() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
            1, 1_000m, PositionEffect.Open);

    // FR-11: 記録は監査台帳へ運ばれる（GoodFaithViolationRecorded の発行）。
    [Fact]
    public async Task 未決済資金による買付は記録され監査イベントが発行される()
    {
        var store = new InMemoryGoodFaithViolationStore();
        using var host = await BuildHostAsync(
            store, new BrokerAccountState(AccountType.Cash), BuyEntry());

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Fill());

        store.GetTally().Count.Should().Be(1);
        var published = session.Sent.MessagesOf<GoodFaithViolationRecorded>().ToList();
        published.Should().ContainSingle();
        published[0].OrderId.Should().Be("ORD-1");
        // 取引日は**米国東部時間**で数える（§4.2 と同じ基準。UTC 日付ではない）。
        published[0].OccurredOn.Should().Be(new DateOnly(2026, 8, 7));
        // 決済済み資金は未供給のまま運ぶ（0 と書くと「残高が 0 だった」と読まれる）。
        published[0].SettledCashInBase.Should().BeNull();
    }

    // **否定形**: 信用口座の約定では記録も発行もしない（監査台帳を無関係な事象で汚さない）。
    [Fact]
    public async Task 信用口座の約定では記録も発行もしない()
    {
        var store = new InMemoryGoodFaithViolationStore();
        using var host = await BuildHostAsync(
            store, new BrokerAccountState(AccountType.Margin), BuyEntry());

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Fill());

        store.GetTally().Count.Should().Be(0);
        session.Sent.MessagesOf<GoodFaithViolationRecorded>().Should().BeEmpty();
    }

    // **否定形**: 口座種別を確認できていなければ記録しない（不明を現金口座とみなさない）。
    [Fact]
    public async Task 口座種別を確認できていなければ記録も発行もしない()
    {
        var store = new InMemoryGoodFaithViolationStore();
        using var host = await BuildHostAsync(store, account: null, approved: BuyEntry());

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Fill());

        store.GetTally().Count.Should().Be(0);
        session.Sent.MessagesOf<GoodFaithViolationRecorded>().Should().BeEmpty();
    }

    // ADR-0013, IADR-0129 決定10: 再配送で二重計上しない（同一チェーンの再試行でも件数が増えない）。
    [Fact]
    public async Task 再配送でも件数は増えない()
    {
        var store = new InMemoryGoodFaithViolationStore();
        using var host = await BuildHostAsync(
            store, new BrokerAccountState(AccountType.Cash), BuyEntry());

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Fill());
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Fill());

        store.GetTally().Count.Should().Be(1);
    }
}
