using RiskManagementService.Common.Abstractions;
using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace RiskManagementService.Tests;

// T-10-2925, FR-10, FR-19, #1396, IADR-0561 決定 2: **Program.cs の実構成**で閉場前の新規建ての締め切りが効くこと（必須依存の配線と構成の読み取り）。
// PoC の事象（MARA を閉場の約 1 時間前〔03:58 JST＝14:58 ET〕に新規建てした）の時刻の 2 分後（15:00 ET）を使う。
// 差し替えるのは時計（固定時刻）・縮退の観測（新規建てが別の理由で止まらないように）だけ。
public class PreCloseEntryCutoffWiringTests
{
    private static readonly DateTimeOffset AtCutoff = new(2026, 10, 19, 19, 0, 0, TimeSpan.Zero); // 15:00 ET（閉場の 60 分前）
    private static readonly DateTimeOffset BeforeCutoff = new(2026, 10, 19, 18, 58, 0, TimeSpan.Zero); // 14:58 ET

    private static OrderIntent SmallBuy(string symbol) =>
        new(symbol, Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
            1, 20m, PositionEffect.Open, StopLossPrice: 19m);

    [Fact]
    public async Task T_10_2925_本番構成で閉場の60分前以降の新規建ては締め切りで拒否されその前は承認される()
    {
        await using var factory = new RiskWorkerWebApplicationFactory { CapitalBaselineSeedNow = AtCutoff };
        var clock = new FakeClock(BeforeCutoff, TradingDay.Of(BeforeCutoff));
        using var wired = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.AddSingleton<IClock>(clock);
            services.RemoveAll<IInformationDegradationStore>();
            services.AddSingleton<IInformationDegradationStore>(FakeInformationDegradation.Affirmed());
        }));

        using var scope = wired.Services.CreateScope();
        var screening = scope.ServiceProvider.GetRequiredService<OrderScreeningService>();

        (await screening.ScreenAsync(new TradeDecisionMade(Guid.NewGuid(), SmallBuy("MSFT"), "判断", BeforeCutoff)))
            .IsApproved.Should().BeTrue("対照: 締め切りの前はほかの統制でも落ちない");

        clock.UtcNow = AtCutoff;
        (await screening.ScreenAsync(new TradeDecisionMade(Guid.NewGuid(), SmallBuy("MARA"), "判断", AtCutoff)))
            .Rejected!.Reasons.Should().Equal(RejectionReason.PreCloseEntryCutoff);
    }

    [Fact]
    public async Task T_10_2925_本番構成は構成の分数を読む()
    {
        await using var factory = new RiskWorkerWebApplicationFactory { CapitalBaselineSeedNow = AtCutoff };
        using var wired = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("PreCloseEntryCutoff:Minutes:UnitedStates", "30");
            b.ConfigureServices(services =>
            {
                services.AddSingleton<IClock>(new FakeClock(AtCutoff, TradingDay.Of(AtCutoff)));
                services.RemoveAll<IInformationDegradationStore>();
                services.AddSingleton<IInformationDegradationStore>(FakeInformationDegradation.Affirmed());
            });
        });

        using var scope = wired.Services.CreateScope();
        var screening = scope.ServiceProvider.GetRequiredService<OrderScreeningService>();

        // 30 分の構成では閉場の 60 分前は締め切りの外。
        (await screening.ScreenAsync(new TradeDecisionMade(Guid.NewGuid(), SmallBuy("MARA"), "判断", AtCutoff)))
            .IsApproved.Should().BeTrue();
    }
}
