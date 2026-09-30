using System.Reflection;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// 🔴 T-10-1840, FR-04, ADR-0048 決定 3, #1118, IADR-0467 決定 6, IADR-0397: 判断の出来高の日足の口は**既定（設定なし）で NoOp**
// （日足の要求 0 回＝取得枠に触れない）。DecisionVolume:Enabled=true かつ OrderExecution:BaseUrl があるときだけ Cached（Http）になり、
// 本番の Program.cs が組んだ判断サービスへ同じ singleton が渡る。有効でも接続先が無ければ NoOp。
public class DailyBarsProviderSelectionTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "http://order-execution")]
    [InlineData("false", "http://order-execution")]
    [InlineData("yes", "http://order-execution")] // 真偽として読めない値は無効
    [InlineData("true", null)]
    [InlineData("true", "not a uri")]
    public void 既定と接続先の無い構成は日足を要求しないNoOp(string? enabled, string? baseUrl)
    {
        using var factory = new Factory(enabled, baseUrl);
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IDailyBarsProvider>();
        provider.Should().BeOfType<NoOpDailyBarsProvider>();
        provider.IsEnabled.Should().BeFalse();
        InjectedInto(scope.ServiceProvider.GetRequiredService<TradeDecisionAppService>()).Should().BeSameAs(provider);
    }

    [Fact]
    public void 有効かつ接続先があればキャッシュつきの口が判断サービスへ渡る()
    {
        using var factory = new Factory("true", "http://order-execution");
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IDailyBarsProvider>();
        provider.Should().BeOfType<CachedDailyBarsProvider>();
        provider.IsEnabled.Should().BeTrue();
        using var other = factory.Services.CreateScope();
        other.ServiceProvider.GetRequiredService<IDailyBarsProvider>().Should().BeSameAs(provider, "キャッシュは singleton で共有する");
        InjectedInto(scope.ServiceProvider.GetRequiredService<TradeDecisionAppService>()).Should().BeSameAs(provider);
    }

    // 🔴 T-10-1845（［2026-10-01 追記］監査 🟡-4）: 本番の組み立ての照会口（名前付きクライアント "order-execution"）の上限は 8 秒。
    // 発注執行が遅れても判断の待ちを 1 銘柄あたり 8 秒に抑える（超えたら未提供。15 分おく）。
    [Fact]
    public void 本番の組み立ての日足の照会の上限は8秒()
    {
        using var factory = new Factory("true", "http://order-execution");
        _ = factory.CreateClient();

        using var http = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("order-execution");
        http.Timeout.Should().Be(TimeSpan.FromSeconds(8));
        HttpDailyBarsSource.RequestTimeout.Should().Be(TimeSpan.FromSeconds(8));
    }

    private static object? InjectedInto(TradeDecisionAppService service) =>
        typeof(TradeDecisionAppService).GetField("_dailyBars", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service);

    private sealed class Factory(string? enabled, string? baseUrl) : WebApplicationFactory<Program>
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
                cfg.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
