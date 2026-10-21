using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using TradeDecisionService.Features.TradeDecision.FlattenBeforeClose;
using TradeDecisionService.Hosted;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// T-10-2933, FR-10, FR-19, #1396, IADR-0561 決定 3・5: 取引判断サービスの**実際の DI 構成**が閉場前の手仕舞い（構成・本体・常駐）を組み、
// 判断のサービスへ市場カレンダー（プロンプトの閉場までの残り時間）を渡すこと。
public class PreCloseFlattenWiringTests
{
    [Fact]
    public void T_10_2933_本番構成は既定で無効の手仕舞いと常駐を組み判断へ市場カレンダーを渡す()
    {
        using var factory = new Factory(new Dictionary<string, string?>());
        _ = factory.CreateClient();

        var options = factory.Services.GetRequiredService<PreCloseFlattenOptions>();
        options.IntervalSeconds.Should().Be(60);
        factory.Services.GetServices<IHostedService>().Should().ContainSingle(s => s is PreCloseFlattenService);

        using var scope = factory.Services.CreateScope();
        var flattener = scope.ServiceProvider.GetRequiredService<PreCloseFlattener>();
        // #1407（PR #1408 のレビュー）: 直近の値の記憶は singleton が渡る（巡回ごとに新しい記憶にならない）。
        typeof(PreCloseFlattener).GetField("_lastKnown", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(flattener).Should().BeSameAs(factory.Services.GetRequiredService<PreCloseFlattenLastKnown>());
        var app = scope.ServiceProvider.GetRequiredService<TradeDecisionAppService>();
        typeof(TradeDecisionAppService).GetField("_marketCalendar", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(app).Should().BeSameAs(factory.Services.GetRequiredService<IMarketCalendar>());
    }

    // 🔴 #1407, IADR-0564 決定 1: 有効化と分数の構成はリスク管理へ移した。取引判断の構成に書けば起動が止まる（黙って読まないにしない）。
    [Fact]
    public void T_10_2933_本番構成は取引判断に残った有効化の構成で起動を止める()
    {
        using var factory = new Factory(new Dictionary<string, string?> { ["PreCloseFlatten:Enabled"] = "true" });

        var act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*リスク管理*");
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

            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
