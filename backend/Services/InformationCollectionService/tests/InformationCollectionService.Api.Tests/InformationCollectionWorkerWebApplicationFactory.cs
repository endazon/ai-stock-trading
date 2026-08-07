using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace AiStockTrading.InformationCollection.Api.Tests;

// WebApplicationFactory（他 Worker テスト準拠）。実 RabbitMQ に依存せず Wolverine の外部トランスポートを無効化する（ADR-0013 / IADR-0129 / #354）。
// 情報源・KB は既定の安全実装（no-op）で外部接続しない。DB は持たない。
// #456: 認可を検査するため TestAuthHandler へ差し替える（実 Keycloak には接続しない）。
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
                ["Auth:Authority"] = "https://localhost/realms/test",
            }));

        builder.ConfigureServices(services =>
        {
            // ADR-0013, IADR-0129, #354: 実 RabbitMQ へ接続しない（Wolverine の外部トランスポートを無効化する）。
            services.DisableAllExternalWolverineTransports();

            // #456: 実 Keycloak を要さずに認可を検査する。ポリシー（OwnerOnly/OwnerOrService）の登録は
            // Program.cs の AddAiStockTradingAuth が済ませており、ここでは**認証スキームだけ**差し替える。
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }
}
