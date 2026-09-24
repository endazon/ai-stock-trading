using RiskManagementService.Common.Abstractions;
using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, #935, IADR-0394 決定7 / T-10-778: **Program.cs の実構成**で、損切りの入力（台帳）が発注審査へ届いていること。
//
// 単体・回帰のテストは OrderScreeningService を自分で組むため、Program.cs の構築式から台帳を外しても
// （あるいは空の台帳へ差し替えても）緑のままである。本テストだけが「本番の DI → 本番の Wolverine ハンドラ発見 →
// 本番の審査」を通しで見る。差し替えるのは時計（固定時刻）と DB（InMemory）と外部トランスポートだけ。
public class StopOutReentryWiringTests
{
    // 2026-09-23 の実測（S1 の発動 13:43:33Z・同じ AAPL の買い 13:46:45Z）。
    private static readonly DateTimeOffset StopOutAt = new(2026, 9, 23, 13, 43, 33, TimeSpan.Zero);
    private static readonly DateTimeOffset BuyAttemptAt = new(2026, 9, 23, 13, 46, 45, TimeSpan.Zero);

    private static OrderIntent Buy(string symbol) =>
        new(symbol, Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
            7, 337.63m, PositionEffect.Open, StopLossPrice: 330m);

    private static SoftwareStopExecuted S1ClosePlaced() =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, SoftwareStopOutcome.ClosePlaced, 707, 337.50m, 337.455m, 1,
            Guid.NewGuid(), "S1-CLOSE",
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
                707, 337.455m, PositionEffect.Close, MarketOrder: true),
            StopOutAt);

    [Fact]
    public async Task 本番構成で損切りを流すと同じ銘柄の買いが名前付きの理由で拒否され計器に出る()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        await using var factory = new RiskWorkerWebApplicationFactory();
        using var wired = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
            services.AddSingleton<IClock>(new FakeClock(BuyAttemptAt, TradingDay.Of(BuyAttemptAt)))));

        // 本番の Wolverine 構成が発見した SoftwareStopExecutedLedgerHandler に台帳へ書かせる。
        await wired.Services.ExecuteAndWaitAsync(async () =>
        {
            using var scope = wired.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(S1ClosePlaced());
        });

        // 本番の DI が組んだ審査（Program.cs の構築式）で判定する。
        using (var scope = wired.Services.CreateScope())
        {
            var screening = scope.ServiceProvider.GetRequiredService<OrderScreeningService>();

            screening.Screen(new TradeDecisionMade(Guid.NewGuid(), Buy("AAPL"), "判断", BuyAttemptAt))
                .Rejected!.Reasons.Should().Contain(RejectionReason.StoppedOutSameDay);

            // 対照: 損切りしていない銘柄には立たない（理由が銘柄の損切りに由来することの確認）。
            var other = screening.Screen(new TradeDecisionMade(Guid.NewGuid(), Buy("MSFT"), "判断", BuyAttemptAt));
            (other.Rejected?.Reasons ?? []).Should().NotContain(RejectionReason.StoppedOutSameDay);
            (other.Rejected?.Reasons ?? []).Should().NotContain(RejectionReason.StopOutStatusUnknown);
        }

        // 判断イベントの購読（TradeDecisionMadeHandler）を通すと、拒否理由が業務メトリクスに名前で出る。
        await wired.Services.ExecuteAndWaitAsync(async () =>
        {
            using var scope = wired.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IMessageBus>()
                .InvokeAsync(new TradeDecisionMade(Guid.NewGuid(), Buy("AAPL"), "判断", BuyAttemptAt));
        });

        capture.TagValuesOf(BusinessMetricNames.RiskRejections, BusinessMetricNames.TagReason)
            .Should().Contain(nameof(RejectionReason.StoppedOutSameDay));
    }
}
