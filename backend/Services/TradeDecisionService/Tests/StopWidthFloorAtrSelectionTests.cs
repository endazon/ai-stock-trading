using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
using TradeDecisionService.Infrastructure.ExternalServices;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// 🔴 T-10-2197, FR-10, FR-04, ADR-0048 決定3, ADR-0049 決定2, #1122, IADR-0486 決定1, IADR-0397: 損切り幅の下限の ATR は**既定（設定なし）で無効**
// （NoAtr＝日足の要求 0 回・2%）。StopWidthFloor:Atr14:Enabled=true かつ OrderExecution:BaseUrl があるときだけ Atr14 になり、
// 日足の口は出来高と**同じ 1 つの singleton**（キャッシュを共有＝取得枠を増やさない）。出来高が無効なら出来高の口は NoOp のまま。
// 本番の Program.cs が組んだ判断サービスと Stage 0 のデコレータへ同じ singleton が渡る。
public class StopWidthFloorAtrSelectionTests
{
    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, "true", null)]
    [InlineData("false", null, "http://order-execution")]
    [InlineData("yes", null, "http://order-execution")] // 真偽として読めない値は無効（DecisionVolume:Enabled と同じ読み）
    [InlineData("true", null, null)]
    [InlineData("true", "true", "not a uri")]
    public void 既定と接続先の無い構成はNoAtrで日足を要求しない(string? atr, string? volume, string? baseUrl)
    {
        using var factory = new Factory(atr, volume, baseUrl);
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var floor = scope.ServiceProvider.GetRequiredService<IStopWidthFloorSource>();
        floor.Should().BeOfType<NoAtrStopWidthFloorSource>();
        floor.IsEnabled.Should().BeFalse();
        InjectedInto(scope.ServiceProvider.GetRequiredService<TradeDecisionAppService>()).Should().BeSameAs(floor);
        Stage0FloorDecorator(scope).FloorSource.Should().BeSameAs(floor, "Stage 0 も同じ singleton（1 か所の選択）");
    }

    [Fact]
    public void ATRだけ有効なら共有の口はCachedで出来高の口はNoOpのまま()
    {
        using var factory = new Factory("true", null, "http://order-execution");
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var floor = scope.ServiceProvider.GetRequiredService<IStopWidthFloorSource>().Should().BeOfType<Atr14StopWidthFloorSource>().Subject;
        floor.IsEnabled.Should().BeTrue();
        floor.DailyBars.Should().BeOfType<CachedDailyBarsProvider>();
        floor.DailyBars.Should().BeSameAs(scope.ServiceProvider.GetRequiredKeyedService<IDailyBarsProvider>(DailyBarsComposition.SharedKey));
        scope.ServiceProvider.GetRequiredService<IDailyBarsProvider>().Should().BeOfType<NoOpDailyBarsProvider>(
            "出来高は DecisionVolume:Enabled が無効なら従来の「未提供」のまま");
        InjectedInto(scope.ServiceProvider.GetRequiredService<TradeDecisionAppService>()).Should().BeSameAs(floor);
        Stage0FloorDecorator(scope).FloorSource.Should().BeSameAs(floor);
    }

    [Fact]
    public void 両方有効なら出来高とATRは同じ日足のsingletonを共有する()
    {
        using var factory = new Factory("true", "true", "http://order-execution");
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        using var other = factory.Services.CreateScope();
        var volume = scope.ServiceProvider.GetRequiredService<IDailyBarsProvider>();
        var floor = scope.ServiceProvider.GetRequiredService<IStopWidthFloorSource>().Should().BeOfType<Atr14StopWidthFloorSource>().Subject;
        volume.Should().BeOfType<CachedDailyBarsProvider>();
        floor.DailyBars.Should().BeSameAs(volume, "キャッシュを共有する（同じ銘柄の同じ取引日の取得は 1 回）");
        other.ServiceProvider.GetRequiredService<IStopWidthFloorSource>().Should().BeSameAs(floor);
    }

    [Fact]
    public void 出来高だけ有効ならATRはNoAtr()
    {
        using var factory = new Factory(null, "true", "http://order-execution");
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IDailyBarsProvider>().Should().BeOfType<CachedDailyBarsProvider>();
        scope.ServiceProvider.GetRequiredService<IStopWidthFloorSource>().Should().BeOfType<NoAtrStopWidthFloorSource>();
    }

    private static object? InjectedInto(TradeDecisionAppService service) =>
        typeof(TradeDecisionAppService).GetField("_stopWidthFloor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service);

    // 監視銘柄のデコレータ（外側）の内側＝損切り幅の下限のデコレータ。
    private static StopWidthFloorAsOfDecisionInputProvider Stage0FloorDecorator(IServiceScope scope)
    {
        var outer = scope.ServiceProvider.GetRequiredService<IAsOfDecisionInputProvider>();
        var inner = (IAsOfDecisionInputProvider)outer.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(IAsOfDecisionInputProvider))
            .GetValue(outer)!;
        return inner.Should().BeOfType<StopWidthFloorAsOfDecisionInputProvider>().Subject;
    }

    private sealed class Factory(string? atr, string? volume, string? baseUrl) : WebApplicationFactory<Program>
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
                if (atr is not null)
                    settings[DailyBarsComposition.StopWidthFloorAtrFlag] = atr;
                if (volume is not null)
                    settings[DailyBarsComposition.DecisionVolumeFlag] = volume;
                if (baseUrl is not null)
                    settings["OrderExecution:BaseUrl"] = baseUrl;
                cfg.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
