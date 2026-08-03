// IADR-0128: consumer は Infrastructure へ移ったが、本ファイルは相対名（Composable.Steps.*）で型を参照している。
// テスト本文を触らずに解決するため、名前空間別名で Composable を Infrastructure 配下へ束ね直す
// （using ディレクティブは入れ子の名前空間を import しないため、別名でなければ解決できない）。
using Composable = AiStockTrading.Notification.Infrastructure.Composable;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiStockTrading.Notification.Api.Tests;

// WebApplicationFactory（他 Worker テスト準拠）。実 RabbitMQ に依存せず MassTransit テストハーネスへ差し替える。
// 送信は既定の安全 sender（no-op）で外部送信しない。DB・認可なし。
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
            services.RemoveAll<IBusControl>();
            services.AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<Composable.Steps.OrderExecutedNotificationConsumer>();
                x.AddConsumer<Composable.Steps.OrderRejectedNotificationConsumer>();
                x.AddConsumer<Composable.Steps.StopLossTriggeredNotificationConsumer>();
                x.AddConsumer<Composable.Steps.AssumptionsChangedNotificationConsumer>();
                x.AddConsumer<Composable.Steps.ReportConfirmedNotificationConsumer>();
                x.AddConsumer<Composable.Steps.CostThresholdReachedNotificationConsumer>();
            });
        });
    }
}
