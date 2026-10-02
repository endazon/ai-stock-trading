using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// 🔴 T-10-2184, NFR, FR-04, FR-11, #1111, IADR-0483 決定3: 取引判断の最中の例外の最終の失敗の報告口が、composition root（Program.cs）で
// **ランタイムから発行する実装へ singleton で 1 つだけ**登録されていることを固定する。報告口は両ハンドラの必須依存なので、
// 登録を消すと組み立てそのものが失敗する（CompositionWiringGuardTests が W0 として拾う）。ここでは実装の型と寿命を固定する
// （scoped の IMessageBus で出す実装へ差し替えると、価格変動のハンドラが投げ直したときに事実が捨てられる）。
public class TradeDecisionFailureReporterRegistrationTests
{
    [Fact]
    public void T_10_2184_最終の失敗の報告口はsingletonの発行の実装で1つだけ登録される()
    {
        using var factory = new Factory();
        _ = factory.CreateClient();

        factory.Services.GetServices<ITradeDecisionFailureReporter>().Should().ContainSingle();
        var singleton = factory.Services.GetRequiredService<ITradeDecisionFailureReporter>();
        singleton.Should().BeOfType<PublishingTradeDecisionFailureReporter>();

        using var a = factory.Services.CreateScope();
        using var b = factory.Services.CreateScope();
        a.ServiceProvider.GetRequiredService<ITradeDecisionFailureReporter>().Should().BeSameAs(singleton);
        b.ServiceProvider.GetRequiredService<ITradeDecisionFailureReporter>().Should().BeSameAs(singleton);
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
