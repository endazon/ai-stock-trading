using RiskManagementService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace RiskManagementService.Tests;

// WebApplicationFactory（platform Worker テスト準拠）。実 RabbitMQ/Postgres/Keycloak に依存せず、
// InMemory DB・Wolverine の外部トランスポート無効化（ADR-0013 / IADR-0129 / #354）・TestAuthHandler へ
// 差し替えてエンドポイントを検証する。
public sealed class RiskWorkerWebApplicationFactory : WebApplicationFactory<Program>
{
    // Factory ごとに一意な InMemory DB 名で他テストと隔離する。
    private readonly string _dbName = Guid.NewGuid().ToString();

    /// <summary>
    /// #257, IADR-0108: Program.cs が**登録時に**読む構成（例 <c>Risk:SimulatorProfile:Enabled</c>）を与える。
    /// <c>ConfigureAppConfiguration</c> の追加分は登録時読み取りに間に合わないため <c>UseSetting</c>（ホスト構成）で渡す。
    /// xUnit の <c>IClassFixture</c> は公開コンストラクタが 1 つであることを要求するため、引数ではなく初期化子で与える。
    /// </summary>
    public IDictionary<string, string?> HostSettings { get; init; } = new Dictionary<string, string?>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in HostSettings)
            builder.UseSetting(key, value);

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

            // ADR-0013, IADR-0129, #354: 実 RabbitMQ へ接続しない（ハンドラの発見は Program.cs 側の配線が担う）。
            services.DisableAllExternalWolverineTransports();

            // Keycloak/JWT に依存せず TestAuthHandler で認証する（既定スキームを Test に切替）。
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    private static void ReplaceDbContextWithInMemory(IServiceCollection services, string dbName)
    {
        var toRemove = services
            .Where(d => d.ServiceType == typeof(DbContextOptions<RiskManagementDbContext>)
                     || (d.ServiceType.IsGenericType
                         && d.ServiceType.GetGenericTypeDefinition().FullName?
                             .Contains("IDbContextOptionsConfiguration") == true
                         && d.ServiceType.GenericTypeArguments.Length == 1
                         && d.ServiceType.GenericTypeArguments[0] == typeof(RiskManagementDbContext)))
            .ToList();
        foreach (var d in toRemove) services.Remove(d);

        services.AddDbContext<RiskManagementDbContext>(opt => opt.UseInMemoryDatabase(dbName));
    }
}
