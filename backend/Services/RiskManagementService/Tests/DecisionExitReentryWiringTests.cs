using RiskManagementService.Common.Abstractions;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.GetEntryBlockers;
using RiskManagementService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Metrics;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Tests;

// T-10-2320, FR-10, #1176, IADR-0495 決定3・4: **Program.cs の実構成**で、判断由来の決済（利確）の印が審査 → 承認 → 台帳 → 次の審査まで届くこと。
//
//   1. 判断の決済（TradeDecisionMade・Close）を本番の購読（TradeDecisionMadeHandler）で審査すると、承認（OrderApproved）に
//      FromTradeDecision が立つ。
//   2. その承認を本番の Wolverine 構成（OrderApprovedLedgerHandler）へ流すと、台帳に由来 TradeDecision で残る。
//   3. 同じ銘柄の買いの新規建ては DecisionExitSameDay で拒否され、計器に名前で出る。新規建ての可否の口も同じ理由を返す（ロング側だけ）。
//   4. owner の手仕舞い（印の無い OrderApproved）で手仕舞った銘柄の買いは止めない。
//
// issue の実例（AMZN を 2026-10-07 02:34:50 JST に利確で全量売却し、5 分後の 02:39:56 に買い直した）の時刻を使う。
// 差し替えるのは時計（固定時刻）・縮退の観測（新規建てが別の理由で止まらないように）・DB（InMemory）・外部トランスポートだけ。
public class DecisionExitReentryWiringTests
{
    private static readonly DateTimeOffset ExitAt = new(2026, 10, 6, 17, 34, 50, TimeSpan.Zero);
    private static readonly DateTimeOffset RebuyAt = new(2026, 10, 6, 17, 39, 56, TimeSpan.Zero);

    // 金額は既定の基準資金（3,000 USD）の段階上限の内側に置く（ほかの統制で拒否されないように）。
    private static OrderIntent SmallBuy(string symbol) =>
        new(symbol, Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
            1, 20m, PositionEffect.Open, StopLossPrice: 19m);

    private static OrderIntent CloseLong(string symbol) =>
        new(symbol, Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.InternalPaper,
            1, 20m, PositionEffect.Close);

    [Fact]
    public async Task T_10_2320_本番構成で判断の利確の後の同じ銘柄の買いは名前付きの理由で拒否され手仕舞いの種類で分かれる()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        await using var factory = new RiskWorkerWebApplicationFactory();
        using var wired = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.AddSingleton<IClock>(new FakeClock(RebuyAt, TradingDay.Of(RebuyAt)));
            services.RemoveAll<IInformationDegradationStore>();
            services.AddSingleton<IInformationDegradationStore>(FakeInformationDegradation.Affirmed());
        }));

        // 手仕舞いの対象となる建玉（AMZN・MSFT を 1 株ずつ）を前日に積む。
        using (var scope = wired.Services.CreateScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IPortfolioLedgerStore>();
            foreach (var symbol in new[] { "AMZN", "MSFT" })
            {
                var entryId = Guid.NewGuid();
                var at = RebuyAt.AddDays(-1);
                ledger.AppendApproval(entryId, SmallBuy(symbol), at, source: ApprovalSource.TradeDecision);
                ledger.AppendFill(entryId, $"open-{entryId:N}", 1, 20m, at);
            }
        }

        // 1. 判断の決済（利確）を本番の購読で審査する → 承認に印が立つ。
        var exitDecision = new TradeDecisionMade(Guid.NewGuid(), CloseLong("AMZN"), "利確（+3%）", ExitAt);
        var approved = await DecideAsync(wired, exitDecision);
        approved.Should().ContainSingle().Which.FromTradeDecision.Should().BeTrue("審査が判断を承認した");

        // 2. 承認を本番の Wolverine 構成へ流す（台帳が判断由来として書く）。owner の手仕舞いは印の無い承認で流す。
        await InvokeAsync(wired, approved[0]);
        await InvokeAsync(wired, new OrderApproved(Guid.NewGuid(), CloseLong("MSFT"), 1, ExitAt));

        using (var scope = wired.Services.CreateScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IPortfolioLedgerStore>();
            ledger.GetCloseApprovals("AMZN", Market.UnitedStates, RebuyAt.AddDays(-2))
                .Should().ContainSingle().Which.Source.Should().Be(ApprovalSource.TradeDecision);
            ledger.GetCloseApprovals("MSFT", Market.UnitedStates, RebuyAt.AddDays(-2))
                .Should().ContainSingle().Which.Source.Should().Be(ApprovalSource.OrderApproved);

            // 3. 本番の DI が組んだ審査で、5 分後の AMZN の買いを判定する。
            var screening = scope.ServiceProvider.GetRequiredService<OrderScreeningService>();
            (await screening.ScreenAsync(new TradeDecisionMade(Guid.NewGuid(), SmallBuy("AMZN"), "買い直し", RebuyAt)))
                .Rejected!.Reasons.Should().Equal(RejectionReason.DecisionExitSameDay);

            // 4. owner の手仕舞いの後の買いは止めない（対照: 同じ構成で承認される＝ほかの統制では落ちない）。
            var afterOwnerClose = await screening.ScreenAsync(
                new TradeDecisionMade(Guid.NewGuid(), SmallBuy("MSFT"), "買い", RebuyAt));
            (afterOwnerClose.Rejected?.Reasons ?? []).Should().BeEmpty();

            // 新規建ての可否の口も同じ理由を返す（ロング側＝買いの新規建てだけ。反対方向は塞がない）。
            var view = scope.ServiceProvider.GetRequiredService<EntryBlockersService>().Build("AMZN", Market.UnitedStates);
            view.LongSide.Should().Equal(RejectionReason.DecisionExitSameDay);
            view.ShortSide.Should().NotContain(RejectionReason.DecisionExitSameDay);
        }

        // 判断イベントの購読（TradeDecisionMadeHandler）を通すと、拒否理由が業務メトリクスに名前で出る。
        await wired.Services.ExecuteAndWaitForTestAsync(async () =>
        {
            using var scope = wired.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IMessageBus>()
                .InvokeAsync(new TradeDecisionMade(Guid.NewGuid(), SmallBuy("AMZN"), "買い直し", RebuyAt));
        });
        capture.TagValuesOf(BusinessMetricNames.RiskRejections, BusinessMetricNames.TagReason)
            .Should().Contain(nameof(RejectionReason.DecisionExitSameDay));
    }

    private static async Task InvokeAsync(WebApplicationFactory<Program> wired, object message) =>
        await wired.Services.ExecuteAndWaitForTestAsync(async () =>
        {
            using var scope = wired.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(message);
        });

    // 判断イベントを本番の購読（TradeDecisionMadeHandler）へ渡し、発行された承認を集める（StopOutReentryWiringTests と同じ作法）。
    private static async Task<IReadOnlyList<OrderApproved>> DecideAsync(WebApplicationFactory<Program> wired, TradeDecisionMade decision)
    {
        using var scope = wired.Services.CreateScope();
        var handler = ActivatorUtilities.CreateInstance<TradeDecisionMadeHandler>(scope.ServiceProvider);
        var bus = new TestMessageContext();
        await handler.Handle(decision, bus, CancellationToken.None);
        return bus.AllOutgoing.OfType<Envelope>().Select(e => e.Message).OfType<OrderApproved>().ToList();
    }
}
