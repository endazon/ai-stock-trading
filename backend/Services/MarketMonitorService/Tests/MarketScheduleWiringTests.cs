using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using MarketMonitorService.Features.MarketMonitor;
using MarketMonitorService.Infrastructure.ExternalServices;
using MarketMonitorService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace MarketMonitorService.Tests;

// FR-03, FR-10, #909, IADR-0380［2026-09-24 追記 / PR #929 監査］F1: 市場監視の**実際の DI 構成**が解決する
// 市場カレンダーを固定する。巡回のテストは偽カレンダー（FakeSchedule）を使うため、`MarketHoursSchedule.IsOpen` を
// 「常に閉場」へ変えても 153 件すべて緑のままだった（監査の実測）。常に閉場は **S1 の評価が黙って止まる**
// （出口を失う）変更であり、テストが緑のまま配線が消える形（PR #919 と同型）である。
// 時刻は引数で渡す（壁時計を読まない）。
public class MarketScheduleWiringTests
{
    [Fact]
    public void T_10_724_実DI構成のカレンダーは取引時間と規則計算の休場日で判定する()
    {
        // T-10-724, FR-03, FR-10, #909, IADR-0380 決定1・決定4
        using var factory = new Factory();
        _ = factory.CreateClient(); // ホスト起動

        var schedule = factory.Services.GetRequiredService<IMarketSchedule>();

        schedule.Should().BeOfType<MarketHoursSchedule>();
        // 2026-09-24（木）14:00Z = 米東 10:00 EDT。場中。
        schedule.IsOpen(Market.UnitedStates, new DateTimeOffset(2026, 9, 24, 14, 0, 0, TimeSpan.Zero))
            .Should().BeTrue("通常取引時間の場中を閉場と読むと S1 の評価が黙って止まる");
        // 2026-11-26（木）= 感謝祭。構成は空でも規則計算で休場。15:00Z = 米東 10:00 EST。
        schedule.IsOpen(Market.UnitedStates, new DateTimeOffset(2026, 11, 26, 15, 0, 0, TimeSpan.Zero))
            .Should().BeFalse("感謝祭は規則計算の休場日（構成で足さなくても閉場）");
    }

    [Fact]
    public void T_10_725_臨時休場日の構成はISOのyyyy_MM_ddだけを受け_月先の表記を読まない()
    {
        // T-10-725, FR-03, #909, IADR-0380［2026-09-24 追記 / PR #929 監査］:
        // "10/09/2026" を invariant culture で読むと月先の 10 月 9 日になる（日先で 9 月 10 日のつもりでも）。
        using var factory = new Factory(new Dictionary<string, string?>
        {
            ["Monitor:Holidays:UnitedStates:0"] = "2026-10-08",
            ["Monitor:Holidays:UnitedStates:1"] = "10/09/2026",
        });
        _ = factory.CreateClient();

        var schedule = factory.Services.GetRequiredService<IMarketSchedule>();

        schedule.IsOpen(Market.UnitedStates, new DateTimeOffset(2026, 10, 8, 14, 0, 0, TimeSpan.Zero))
            .Should().BeFalse("ISO で書いた臨時休場日は足される");
        schedule.IsOpen(Market.UnitedStates, new DateTimeOffset(2026, 10, 9, 14, 0, 0, TimeSpan.Zero))
            .Should().BeTrue("ISO でない表記は読まない（月先で 10 月 9 日を休場にしない）");
    }

    private sealed class Factory(IDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");
            builder.UseSetting("Auth:Authority", "https://localhost/realms/test");
            foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
                builder.UseSetting(key, value);

            builder.ConfigureServices(services =>
            {
                var toRemove = services
                    .Where(d => d.ServiceType == typeof(DbContextOptions<MarketMonitorDbContext>)
                             || (d.ServiceType.IsGenericType
                                 && d.ServiceType.GetGenericTypeDefinition().FullName?
                                     .Contains("IDbContextOptionsConfiguration") == true
                                 && d.ServiceType.GenericTypeArguments.Length == 1
                                 && d.ServiceType.GenericTypeArguments[0] == typeof(MarketMonitorDbContext)))
                    .ToList();
                foreach (var d in toRemove) services.Remove(d);
                services.AddDbContext<MarketMonitorDbContext>(opt => opt.UseInMemoryDatabase(_dbName));

                // ADR-0013, IADR-0129, #354: 実 RabbitMQ へ接続しない。
                services.DisableAllExternalWolverineTransports();

                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            });
        }
    }
}
