using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// 🔴 NFR-13, ADR-0074 決定2, IADR-0639: **本番の Program.cs の組み立て**で、照会できないときの段（CostControl:HaltQuery:UnavailableMode）が
// LLM 費用の停止の関所を切り替えることを固定する。既定（Off）は従来の関所のまま、Enforce は fail-closed の関所で包む。
public class LlmCostHaltUnavailableModeWiringTests
{
    [Fact]
    public void 既定は従来の関所のまま()
    {
        using var factory = new Factory(new() { ["CostControl:BaseUrl"] = "http://cost-control-service:8080" });
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ILlmCostHaltGate>().Should().BeOfType<HttpLlmCostHaltGate>();
    }

    [Theory]
    [InlineData("Report")]
    [InlineData("Enforce")]
    public void Report_と_Enforce_は_fail_closed_の関所で包む(string mode)
    {
        using var factory = new Factory(new()
        {
            ["CostControl:BaseUrl"] = "http://cost-control-service:8080",
            [LlmCostHaltQueryOptions.ModeKey] = mode,
        });
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ILlmCostHaltGate>().Should().BeOfType<FailClosedLlmCostHaltGate>();
    }

    // 組み立てた関所で実際に呼ぶ: 照会の宛先に届かない（名前解決しない予約の TLD）＝読めない → Enforce は停止として扱う。
    [Fact]
    public async Task Enforce_で照会の宛先に届かなければ停止として扱う()
    {
        using var factory = new Factory(new()
        {
            ["CostControl:BaseUrl"] = "http://cost-control.invalid:8080",
            [LlmCostHaltQueryOptions.ModeKey] = "Enforce",
        });
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<ILlmCostHaltGate>().IsHaltedAsync(TestContext.Current.CancellationToken))
            .Should().BeTrue("一度も読めていない＝猶予の外（fail-closed）");
    }

    // 陰性対照: 既定（Off）の関所は、同じく届かないとき停止していないへ倒す（従来どおり）。
    [Fact]
    public async Task 既定で照会の宛先に届かなければ停止していない()
    {
        using var factory = new Factory(new() { ["CostControl:BaseUrl"] = "http://cost-control.invalid:8080" });
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<ILlmCostHaltGate>().IsHaltedAsync(TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }

    // Enforce なのに照会の宛先が無いと、NoOp（常に停止していない）へ黙って倒さず起動で止める。
    [Fact]
    public void Enforce_で照会の宛先が無ければ起動で止める()
    {
        using var factory = new Factory(new() { [LlmCostHaltQueryOptions.ModeKey] = "Enforce" });

        var act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*CostControl:BaseUrl*");
    }

    [Fact]
    public void 読めない段は起動で止める()
    {
        using var factory = new Factory(new() { [LlmCostHaltQueryOptions.ModeKey] = "Block" });

        var act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{LlmCostHaltQueryOptions.ModeKey}*");
    }

    private sealed class Factory(Dictionary<string, string> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            foreach (var (k, v) in settings)
                builder.UseSetting(k, v);
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:ConnectionString"] = "amqp://localhost",
                ["Otlp:Endpoint"] = "http://localhost:4317",
            }));
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
            builder.ConfigureTestServices(services => services.UseFixedClock(TestHostClock.OutsideMarketHours));
        }
    }
}
