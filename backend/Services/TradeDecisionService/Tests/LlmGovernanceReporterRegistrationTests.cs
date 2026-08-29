using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-04, FR-09, FR-11, ADR-0017 決定2/決定4, #335, #595, IADR-0216/0217:
// 割当統制の可観測性ポートが composition root で **ちょうど 1 つ**登録されていることを固定する。
//
// 🔴 **本テストの存在理由は「解決できること」と「登録が 1 つであること」が別の事実だからである。**
//   #595 では `Program.cs` が `ILlmGovernanceReporter` を**コメントごとバイト一致の同一ブロックで 2 回**
//   登録していた。既存の配線テスト 16 本はいずれも `GetRequiredService` で 1 つ解決して型を確かめる形で、
//   `GetRequiredService` は**最後の登録**を返すため、**二重登録があっても全部緑のままだった**。
//
// 退行したときに何が起きるか:
//   `PublishingLlmGovernanceReporter` は **publish する実装**である。いつか列挙
//   （`IEnumerable<ILlmGovernanceReporter>`）で全実装へ配る形になったとき、
//   `LlmFallbackFired` / `TradeDecisionSkipped` が **2 通ずつ発行される**。
//   発行の重複は下流（監査台帳・通知・月報の件数）を静かに 2 倍にするため、発見が遅れる型の欠陥である。
public class LlmGovernanceReporterRegistrationTests
{
    // 🔴 本体。重複を再導入すると 2 件になって落ちる（`GetRequiredService` 型のテストでは捕まらない性質）。
    [Fact]
    public void 割当統制の可観測性ポートの登録はちょうど一つである()
    {
        using var factory = new Factory();
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetServices<ILlmGovernanceReporter>().Should().ContainSingle();
    }

    // 対の肯定形。個数だけを見ると「1 つだが No-op へ落ちている」壊れ方を見逃す。
    [Fact]
    public void 割当統制の可観測性ポートは発行実装へ結線される()
    {
        using var factory = new Factory();
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ILlmGovernanceReporter>()
            .Should().BeOfType<PublishingLlmGovernanceReporter>()
            .And.NotBeOfType<NoOpLlmGovernanceReporter>();
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");

            builder.ConfigureServices(services =>
            {
                // ADR-0013, IADR-0129, #354: 実 RabbitMQ を避けて Wolverine の外部トランスポートを無効化する。
                services.DisableAllExternalWolverineTransports();
            });
        }
    }
}
