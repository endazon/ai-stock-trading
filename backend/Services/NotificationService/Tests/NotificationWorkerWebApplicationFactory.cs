using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace NotificationService.Tests;

// WebApplicationFactory（他 Worker テスト準拠）。実 RabbitMQ に依存せず、Wolverine の外部トランスポートを
// 無効化する（ADR-0013 / IADR-0129 / #354）。送信は既定の安全 sender（no-op）で外部送信しない。DB・認可なし。
//
// ハンドラの発見は Program.cs 側の配線（Infrastructure アセンブリ）が担うため、テスト側で購読を
// 列挙し直す必要が無くなった（MassTransit 時代はここで 6 件だけ登録しており本番の 10 件とずれていた。
// Wolverine では発見範囲が本番と同一になり、テストと本番のズレが構造的に消える）。
public sealed class NotificationWorkerWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:ConnectionString"] = "amqp://localhost",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                // Notifications:Provider 未設定 → 安全既定（no-op）。
            }));

        builder.ConfigureServices(services =>
        {
            // 実 RabbitMQ へ接続せず、送信は stub エンドポイントへ記録される（宛先 URI は保たれる）。
            services.DisableAllExternalWolverineTransports();
        });
    }
}
