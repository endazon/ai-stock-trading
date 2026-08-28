using AuditService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine;

namespace AuditService.Tests;

// WebApplicationFactory（他 Worker テスト準拠）。実 RabbitMQ/Postgres/Keycloak に依存せず、InMemory DB・
// Wolverine の外部トランスポート無効化（ADR-0013 / IADR-0129 / #354）・TestAuthHandler へ差し替えて
// イベント記録と照会エンドポイントを検証する。
public sealed class AuditWorkerWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:ConnectionString"] = "amqp://localhost",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test",
            }));

        builder.ConfigureServices(services =>
        {
            ReplaceDbContextWithInMemory(services, _dbName);

            // ADR-0013, IADR-0129, #354: 実 RabbitMQ へ接続しない。ハンドラの発見は Program.cs 側の配線
            // （Infrastructure アセンブリ全体）が担うため、テスト側で購読を列挙し直す必要が無くなった
            // （MassTransit 時代はここで 8 件だけ登録しており、本番の 21 件とずれていた）。
            services.DisableAllExternalWolverineTransports();

            // Keycloak/JWT に依存せず TestAuthHandler で認証する（既定スキームを Test に切替）。
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    private static void ReplaceDbContextWithInMemory(IServiceCollection services, string dbName)
    {
        var toRemove = services
            .Where(d => d.ServiceType == typeof(DbContextOptions<AuditDbContext>)
                     || (d.ServiceType.IsGenericType
                         && d.ServiceType.GetGenericTypeDefinition().FullName?
                             .Contains("IDbContextOptionsConfiguration") == true
                         && d.ServiceType.GenericTypeArguments.Length == 1
                         && d.ServiceType.GenericTypeArguments[0] == typeof(AuditDbContext)))
            .ToList();
        foreach (var d in toRemove) services.Remove(d);

        services.AddDbContext<AuditDbContext>(opt => opt.UseInMemoryDatabase(dbName));
    }
}
