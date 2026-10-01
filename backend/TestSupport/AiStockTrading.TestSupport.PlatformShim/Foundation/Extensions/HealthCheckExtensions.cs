using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;

// IADR-0011（platform の最小移植・由来: Foundation/Extensions/HealthCheckExtensions.cs）:
// Kubernetes の liveness/readiness プローブ向けエンドポイント。
public static class HealthCheckExtensions
{
    /// <summary>
    /// NFR, IADR-0468, #1137: readiness の DB 疎通チェック（<c>AddNpgSql</c>・"ready" タグ）1 回の打ち切り時間。
    /// <para>
    /// 未指定だと打ち切りは Npgsql の既定（接続 15 秒・コマンド 30 秒）に委ねられ、kubelet の readinessProbe の
    /// <c>timeoutSeconds</c>（未指定なら 1 秒）が先に切って「結果の無い失敗」になる（2026-09-30 17:49 UTC の
    /// "completed after 1002ms … The operation was canceled" はこの形）。
    /// 🔴 <b>chart の <c>probes.readiness.timeoutSeconds</c>（values.yaml）より厳密に短く保つ</b>——チェックが先に
    /// 打ち切って 503（Unhealthy）を返し、kubelet の打ち切りより先に答えが届くようにする（Npgsql が取り消しに応じる局面に限る。
    /// 起動・認証のハンドシェイクでの無応答は接続 Timeout まで止まらない。IADR-0468 の残余）。
    /// 両者の大小は <c>ReadinessProbeTimeoutConsistencyTests</c>（PlatformShim.Tests）と helm.yml の assert が固定する。
    /// </para>
    /// </summary>
    public static readonly TimeSpan NpgSqlReadinessTimeout = TimeSpan.FromSeconds(3);

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
