using System.Reflection;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// 🔴 UC-02, FR-03, #1077, IADR-0451 決定4: 判断後の見送りの発行ポート（IDecisionHeldReporter）が composition root で
// **ちょうど 1 つ・発行実装へ**登録され、判断サービスへ実際に届いていることを固定する。
//
// 判断サービス側の依存は省略可能（既定 NoOp）である。Program.cs の登録を消してもコンパイルは通り、DI は NoOp で組み、
// DecisionHeldReportTests は全緑のまま **Hold が続く間の UC-02 が再び発火しなくなる**（#1077 の症状そのもの）。
// DecisionSkipReporterRegistrationTests と同じ 3 点（個数・実装型・判断サービスが保持する実体）で塞ぐ。
public class DecisionHeldReporterRegistrationTests
{
    [Fact]
    public void 判断後の見送りの発行ポートの登録はちょうど一つである()
    {
        using var factory = new Factory();
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetServices<IDecisionHeldReporter>().Should().ContainSingle();
    }

    [Fact]
    public void 判断後の見送りの発行ポートは発行の実装へ結線される()
    {
        using var factory = new Factory();
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IDecisionHeldReporter>()
            .Should().BeOfType<PublishingDecisionHeldReporter>()
            .And.NotBeOfType<NoOpDecisionHeldReporter>();
    }

    [Fact]
    public void DIが組んだ判断サービスは発行の実装を保持する()
    {
        using var factory = new Factory();
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TradeDecisionAppService>();

        var field = typeof(TradeDecisionAppService).GetField(
            "_heldReporter", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull("判断サービスは判断後の見送りの発行ポートをフィールドで保持する");

        field!.GetValue(service).Should().BeOfType<PublishingDecisionHeldReporter>();
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");

            // ADR-0013, IADR-0129: 実 RabbitMQ を避けて Wolverine の外部トランスポートを無効化する。
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
