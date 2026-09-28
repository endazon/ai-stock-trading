using System.Reflection;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// 🔴 FR-04, ADR-0020 決定2, #1081, IADR-0453: ニュースの状態のストアが composition root で **singleton として 1 つ**登録され、
// 判断サービスが**その同じ実体**を保持することを固定する。
//
// 判断サービス側の依存は省略可能（既定 null＝「ニュース: 不明」）である。Program.cs の登録を消してもコンパイルは通り、
// NewsStatusInPromptTests は全緑のまま、本番のプロンプトは常に「不明」になる（欠測・未構成の明示が届かない）。
// スコープ登録に変えると、定時の購読が記録した値が別スコープの判断へ届かない。
public class NewsCollectionStatusStoreRegistrationTests
{
    [Fact]
    public void 判断サービスは購読が記録するのと同じストアの実体を保持する()
    {
        using var factory = new Factory();
        _ = factory.CreateClient();

        factory.Services.GetServices<NewsCollectionStatusStore>().Should().ContainSingle();
        var singleton = factory.Services.GetRequiredService<NewsCollectionStatusStore>();

        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TradeDecisionAppService>();
        var field = typeof(TradeDecisionAppService).GetField("_newsStatus", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull("判断サービスはニュースの状態のストアをフィールドで保持する");

        field!.GetValue(service).Should().BeSameAs(singleton, "購読が記録した値が判断へ届くには同じ実体でなければならない");
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
