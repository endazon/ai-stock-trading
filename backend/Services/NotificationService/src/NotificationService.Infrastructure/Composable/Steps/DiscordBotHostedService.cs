using AiStockTrading.Notification.Application.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Notification.Infrastructure.Composable.Steps;

// FR-14, IADR-0062: Bot Gateway の常駐をホストのライフサイクルへ結ぶ。
// 既定の実装は NullDiscordBotGateway（接続しない）のため、本サービスは既定では何もしない。
//
// 接続失敗で host を落とさない: 通知（FR-09 アウトバウンド）は Bot と独立して動作すべきであり、
// Discord 障害で通知サービス全体が停止すると可用性 NFR に反する（詳細設計07「Discord 自体の障害への依存」）。
internal sealed class DiscordBotHostedService(
    IDiscordBotGateway gateway,
    ILogger<DiscordBotHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await gateway.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Discord Bot の起動に失敗しました（通知の送信は継続します）。");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await gateway.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Discord Bot の停止で例外が発生しました。");
        }
    }
}
