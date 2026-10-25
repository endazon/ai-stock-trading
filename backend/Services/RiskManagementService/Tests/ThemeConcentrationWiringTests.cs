using RiskManagementService.Common.Abstractions;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace RiskManagementService.Tests;

// T-10-2939, FR-10, FR-19, #1397, IADR-0562 決定 2: **Program.cs の実構成**で、テーマごとの同時保有の上限が効くこと（定義の配線・未約定の新規建て〔予約〕を数える）。
// PoC の事象（2026-10-16 に MARA・MSTR・COIN・HOOD を短時間に次々と建てた）を、承認行が届く前（予約だけがある間）の連続した審査で再現する。
// 差し替えるのは時計（固定時刻。閉場前の締め切りの外）・縮退の観測だけ。
public class ThemeConcentrationWiringTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 15, 0, 0, TimeSpan.Zero); // 11:00 ET

    private static OrderIntent SmallBuy(string symbol) =>
        new(symbol, Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
            1, 20m, PositionEffect.Open, StopLossPrice: 19m);

    [Fact]
    public async Task T_10_2939_本番構成で同じ集まりの2銘柄目の新規建ては予約の間でも拒否され別の集まりは通る()
    {
        await using var factory = new RiskWorkerWebApplicationFactory { CapitalBaselineSeedNow = At };
        using var wired = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.AddSingleton<IClock>(new FakeClock(At, TradingDay.Of(At)));
            services.RemoveAll<IInformationDegradationStore>();
            services.AddSingleton<IInformationDegradationStore>(FakeInformationDegradation.Affirmed());
        }));

        wired.Services.GetRequiredService<ThemeConcentrationSettings>().Themes.Select(t => t.Name)
            .Should().Equal("暗号資産", "AI・半導体");

        using var scope = wired.Services.CreateScope();
        var screening = scope.ServiceProvider.GetRequiredService<OrderScreeningService>();

        // 1 件目（MARA）は承認され予約が書かれる（承認行はまだ無い）。
        (await screening.ScreenAsync(new TradeDecisionMade(Guid.NewGuid(), SmallBuy("MARA"), "判断", At)))
            .IsApproved.Should().BeTrue();

        // 2 件目（COIN・同じ暗号資産）は予約の MARA を数えて拒否される。
        foreach (var symbol in new[] { "COIN", "MSTR", "HOOD" })
        {
            (await screening.ScreenAsync(new TradeDecisionMade(Guid.NewGuid(), SmallBuy(symbol), "判断", At)))
                .Rejected!.Reasons.Should().Equal(RejectionReason.ThemeConcentrationExceeded);
        }

        // 別の集まり（NVDA）・集まりに属さない銘柄（AAPL）は通る。
        (await screening.ScreenAsync(new TradeDecisionMade(Guid.NewGuid(), SmallBuy("NVDA"), "判断", At))).IsApproved.Should().BeTrue();
        (await screening.ScreenAsync(new TradeDecisionMade(Guid.NewGuid(), SmallBuy("AAPL"), "判断", At))).IsApproved.Should().BeTrue();
    }
}
