using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-02, FR-03, #909, IADR-0380 決定5［2026-09-24 追記 / PR #929 監査］: 取引判断サービスの**実際の DI 構成**が解決する
// 市場カレンダーと、臨時休場日の構成（TradeCycle:Holidays:<Market>）の読み方を固定する。市場監視の
// Monitor:Holidays と同じ読み方（ISO の yyyy-MM-dd だけ）でないと、2 つのサービスが「今は開場か」で食い違う。
public class MarketCalendarWiringTests
{
    [Fact]
    public void T_10_728_臨時休場日の構成はISOのyyyy_MM_ddだけを受け_月先の表記を読まない()
    {
        // T-10-728, FR-02, FR-03, #909, IADR-0380［2026-09-24 追記 / PR #929 監査］
        using var factory = new Factory(new Dictionary<string, string?>
        {
            ["TradeCycle:Holidays:UnitedStates:0"] = "2026-10-08",
            ["TradeCycle:Holidays:UnitedStates:1"] = "10/09/2026",
        });
        _ = factory.CreateClient();

        var calendar = factory.Services.GetRequiredService<IMarketCalendar>();

        calendar.Should().BeOfType<MarketCalendar>();
        calendar.IsOpen(Market.UnitedStates, new DateTimeOffset(2026, 10, 8, 14, 0, 0, TimeSpan.Zero))
            .Should().BeFalse("ISO で書いた臨時休場日は足される");
        calendar.IsOpen(Market.UnitedStates, new DateTimeOffset(2026, 10, 9, 14, 0, 0, TimeSpan.Zero))
            .Should().BeTrue("ISO でない表記は読まない（月先で 10 月 9 日を休場にしない）");
        calendar.IsOpen(Market.UnitedStates, new DateTimeOffset(2026, 11, 26, 15, 0, 0, TimeSpan.Zero))
            .Should().BeFalse("感謝祭は構成に書かなくても規則計算の休場日");
    }

    private sealed class Factory(IDictionary<string, string?> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);

            // ADR-0013, IADR-0129, #354: 実 RabbitMQ へ接続しない。
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
