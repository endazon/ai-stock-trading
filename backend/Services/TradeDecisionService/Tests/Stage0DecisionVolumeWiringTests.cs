using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
using TradeDecisionService.Infrastructure.ExternalServices;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// 🔴 T-10-2037, FR-04, FR-15, ADR-0048 決定 2, #1139, IADR-0479 決定 3, IADR-0397: 本番の組み立てで、Stage 0 の as-of 入力の供給は
// 監視銘柄のデコレータ →出来高のデコレータ → 既定の「入力なし」の順に組まれ、出来高のデコレータには**判断サービスと同じ singleton の日足の口**が渡る
// （既定は NoOp＝要求 0 回、DecisionVolume:Enabled=true と接続先があれば Cached）。2 か所目の切り替えを作らない。
public class Stage0DecisionVolumeWiringTests
{
    [Theory]
    [InlineData(null, null, typeof(NoOpDailyBarsProvider))]
    [InlineData("false", "http://order-execution", typeof(NoOpDailyBarsProvider))]
    [InlineData("true", null, typeof(NoOpDailyBarsProvider))]
    [InlineData("true", "http://order-execution", typeof(CachedDailyBarsProvider))]
    public void asof供給は出来高のデコレータを挟み判断と同じ日足の口を使う(string? enabled, string? baseUrl, Type expectedProvider)
    {
        using var factory = new Factory(enabled, baseUrl);
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var outer = scope.ServiceProvider.GetRequiredService<IAsOfDecisionInputProvider>();
        outer.Should().BeOfType<WatchlistAsOfDecisionInputProvider>();

        // ［2026-10-03 追記 / #1122, IADR-0486 決定4］監視銘柄 → 損切り幅の下限（ATR）→ 出来高 → 入力なし の順。
        var floor = InnerOf(outer).Should().BeOfType<StopWidthFloorAsOfDecisionInputProvider>().Subject;
        var volume = floor.Inner.Should().BeOfType<DailyVolumeAsOfDecisionInputProvider>().Subject;
        volume.Inner.Should().BeOfType<NoAsOfDecisionInputProvider>();
        volume.DailyBars.Should().BeOfType(expectedProvider);
        volume.DailyBars.Should().BeSameAs(scope.ServiceProvider.GetRequiredService<IDailyBarsProvider>(),
            "判断サービスと同じ singleton（同じ設定の 1 か所の選択）");
    }

    // FR-15, #1308, IADR-0528 決定7: 連鎖の最も内側は、方針（Reports:BaseUrl）と日足（OrderExecution:BaseUrl）の接続先がそろったときだけ
    // 基底の供給になる。片方でも欠ければ従来の「入力なし」（記録は 0 件・LLM 0 回）。
    [Theory]
    [InlineData(null, null, typeof(NoAsOfDecisionInputProvider))]
    [InlineData("http://report", null, typeof(NoAsOfDecisionInputProvider))]
    [InlineData(null, "http://order-execution", typeof(NoAsOfDecisionInputProvider))]
    [InlineData("not-a-url", "http://order-execution", typeof(NoAsOfDecisionInputProvider))]
    [InlineData("http://report", "http://order-execution", typeof(BaseAsOfDecisionInputProvider))]
    public void 連鎖の最も内側は方針と日足の接続先がそろったときだけ基底の供給になる(string? reports, string? orderExecution, Type expected)
    {
        using var factory = new Factory(enabled: null, baseUrl: orderExecution, reportsBaseUrl: reports);
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var outer = scope.ServiceProvider.GetRequiredService<IAsOfDecisionInputProvider>();
        var floor = (StopWidthFloorAsOfDecisionInputProvider)InnerOf(outer);
        var volume = (DailyVolumeAsOfDecisionInputProvider)floor.Inner;
        volume.Inner.Should().BeOfType(expected);
    }

    // 監視銘柄のデコレータは primary constructor の引数を捕捉している（内側は IAsOfDecisionInputProvider 型のフィールドが 1 つ）。
    private static IAsOfDecisionInputProvider InnerOf(IAsOfDecisionInputProvider outer) =>
        (IAsOfDecisionInputProvider)outer.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(IAsOfDecisionInputProvider))
            .GetValue(outer)!;

    private sealed class Factory(string? enabled, string? baseUrl, string? reportsBaseUrl = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["RabbitMq:ConnectionString"] = "amqp://localhost",
                    ["Otlp:Endpoint"] = "http://localhost:4317",
                };
                if (enabled is not null)
                    settings["DecisionVolume:Enabled"] = enabled;
                if (baseUrl is not null)
                    settings["OrderExecution:BaseUrl"] = baseUrl;
                if (reportsBaseUrl is not null)
                    settings["Reports:BaseUrl"] = reportsBaseUrl;
                cfg.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
