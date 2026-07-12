using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiStockTrading.InformationCollection.Worker.Tests;

// WebApplicationFactory（他 Worker テスト準拠）。実 RabbitMQ に依存せず MassTransit テストハーネスへ差し替える。
// 情報源・KB は既定の安全実装（no-op）で外部接続しない。DB・認可なし。
public sealed class InformationCollectionWorkerWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:ConnectionString"] = "amqp://localhost",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                // 巡回が起動直後に走らないよう十分長い間隔にする（起動検証のみ）。
                ["Collection:PollIntervalSeconds"] = "3600",
                // Collection:Source:Provider 未設定 → NoOpInformationSource（外部接続しない）。
            }));

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IBusControl>();
            services.AddMassTransitTestHarness();
        });
    }
}
