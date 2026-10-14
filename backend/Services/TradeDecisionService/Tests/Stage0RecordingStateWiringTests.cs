using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
using TradeDecisionService.Infrastructure.ExternalServices;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-15, NFR（費用）, ADR-0033 決定5, #1342, IADR-0537, IADR-0397: 本番の組み立てで、記録器の状態の置き場は
// Stage0Recording:StateDirectory があるときだけファイル（永続ボリューム）になり、無ければ「読めない・書けない」既定になる。
// 印と台帳は同じ置き場の別のファイルであり、同じインスタンスが 2 つのポートを持つ。
public class Stage0RecordingStateWiringTests
{
    [Theory]
    [InlineData(null, typeof(NoStage0RecordingStateStore))]
    [InlineData(" ", typeof(NoStage0RecordingStateStore))]
    [InlineData("/var/lib/ai-stock-trading/stage0/state", typeof(FileStage0RecordingStateStore))]
    public void 状態の置き場は構成があるときだけファイルになる(string? stateDirectory, Type expected)
    {
        using var factory = new Factory(stateDirectory);
        _ = factory.CreateClient();

        var state = factory.Services.GetRequiredService<IStage0RecordingStateStore>();
        state.Should().BeOfType(expected);
        factory.Services.GetRequiredService<IStage0RecordingCostLedger>().Should().BeSameAs(state);
    }

    // 🔴 既定（置き場なし）は、印も台帳も「読めない・書けない」と答え、ロックも取れない（記録器は LLM を呼ばない）。
    [Fact]
    public async Task 置き場なしの既定は読めず書けずロックも取れない()
    {
        var none = new NoStage0RecordingStateStore();
        var ct = TestContext.Current.CancellationToken;

        none.TryAcquireRunLock().Should().BeNull();
        (await none.LoadAsync("run", ct)).Phase.Should().Be(Stage0RunPhase.Unreadable);
        (await none.MarkStartedAsync("run", ct)).Should().BeFalse();
        (await none.ReadAsync("ledger", ct)).Should().BeNull();
        (await none.WriteAsync("ledger", 1m, ct)).Should().BeFalse();
    }

    private sealed class Factory(string? stateDirectory) : WebApplicationFactory<Program>
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
                if (stateDirectory is not null)
                    settings["Stage0Recording:StateDirectory"] = stateDirectory;
                cfg.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
