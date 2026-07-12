using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;

// IADR-0011（platform の最小移植・由来: Foundation/Extensions/HealthCheckExtensions.cs）:
// Kubernetes の liveness/readiness プローブ向けエンドポイント。
public static class HealthCheckExtensions
{
    public static IHealthChecksBuilder AddAiStockTradingHealthChecks(
        this IServiceCollection services) =>
        services.AddHealthChecks();

    public static WebApplication MapAiStockTradingHealthChecks(
        this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Liveness: プロセスが生きているか（依存不要）
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false
        });
        // Readiness: 依存サービスへの疎通確認（"ready" タグを付けたチェックのみ）
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = hc => hc.Tags.Contains("ready")
        });
        return app;
    }
}
