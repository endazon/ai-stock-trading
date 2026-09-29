using System.Reflection;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using TradeDecisionService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Infrastructure.Composable.Observability;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// 🔴 T-10-1774, NFR, FR-04, FR-10, #1092, IADR-0462 決定3・決定4: LLM を呼ぶ前の見送りの発行ポートと、保有照会の状態の報告口が
// composition root で**発行の実装へ**登録され、判断サービスへ実際に届いていることを固定する。
//
// 判断サービス側の依存は省略可能（既定 NoOp）である。Program.cs の登録を消してもコンパイルは通り、LedgerGapEventsTests は全緑のまま
// 台帳には何も出ない（#1092 の症状の再発）。報告口は singleton で、スコープごとに作られる判断サービスの間で状態を共有する。
public class LedgerGapReporterRegistrationTests
{
    [Fact]
    public void T_10_1774_LLMを呼ぶ前の見送りの発行ポートは発行の実装へ1つだけ結線され判断サービスが保持する()
    {
        using var factory = new Factory();
        _ = factory.CreateClient();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetServices<IDecisionForgoneBeforeLlmReporter>().Should().ContainSingle();
        scope.ServiceProvider.GetRequiredService<IDecisionForgoneBeforeLlmReporter>()
            .Should().BeOfType<PublishingDecisionForgoneBeforeLlmReporter>();

        var service = scope.ServiceProvider.GetRequiredService<TradeDecisionAppService>();
        Field(service, "_forgoneReporter").Should().BeOfType<PublishingDecisionForgoneBeforeLlmReporter>();
    }

    [Fact]
    public void T_10_1774_保有照会の状態の報告口はsingletonの発行の実装で_スコープをまたいで同じ実体が判断サービスへ届く()
    {
        using var factory = new Factory();
        _ = factory.CreateClient();

        factory.Services.GetServices<IPositionQueryHealthReporter>().Should().ContainSingle();
        var singleton = factory.Services.GetRequiredService<IPositionQueryHealthReporter>();
        singleton.Should().BeOfType<PositionQueryHealthReporter>();

        using var a = factory.Services.CreateScope();
        using var b = factory.Services.CreateScope();
        Field(a.ServiceProvider.GetRequiredService<TradeDecisionAppService>(), "_positionQueryHealth").Should().BeSameAs(singleton);
        Field(b.ServiceProvider.GetRequiredService<TradeDecisionAppService>(), "_positionQueryHealth").Should().BeSameAs(singleton);
    }

    private static object? Field(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull($"判断サービスは {name} をフィールドで保持する");
        return field!.GetValue(target);
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
