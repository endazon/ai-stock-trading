using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
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

    // 🔴 T-10-2187, NFR, FR-04, FR-11, #1111, IADR-0483 決定3: **Program.cs が組み立てた**報告口で報告すると、TradeDecisionFailed が
    // メッセージバスへ実際に発行される（独立監査 🟡2）。上の試験は型と寿命しか見ず、発行の試験（TradeDecisionFailureRecordTests）は
    // Program.cs と同じ形の委譲を自分で組み直していたため、Program.cs の発行の委譲を何もしない形へ変えても緑のままだった。
    // ここでは本番の組み立てから解決した報告口を使い、Wolverine の追跡で外へ出たメッセージを見る（外部の送信先は無効化してある）。
    [Fact]
    public async Task T_10_2187_本番の組み立ての報告口で報告するとTradeDecisionFailedがメッセージバスへ発行される()
    {
        using var factory = new Factory();
        _ = factory.CreateClient();
        var reporter = factory.Services.GetRequiredService<ITradeDecisionFailureReporter>();

        var session = await factory.Services.ExecuteAndWaitForTestAsync(() => reporter.ReportFinalFailureAsync(
            "price-movement", "MSFT", Market.UnitedStates, new TimeoutException("照会に失敗 SECRET-2187")));

        var failed = session.Sent.MessagesOf<TradeDecisionFailed>().Should().ContainSingle(
            "Program.cs の発行の委譲がランタイムの MessageBus から 1 件発行する").Which;
        failed.Symbol.Should().Be("MSFT");
        failed.Market.Should().Be(Market.UnitedStates);
        failed.CycleTrigger.Should().Be("price-movement");
        failed.ExceptionType.Should().Be(typeof(TimeoutException).FullName);
        failed.EventId.Should().NotBe(Guid.Empty);
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
