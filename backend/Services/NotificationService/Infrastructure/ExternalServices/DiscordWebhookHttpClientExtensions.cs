using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NotificationService.Infrastructure.ExternalServices;

// #289, FR-09, IADR-0020: Discord Webhook 送信専用の名前付き HttpClient。
//
// Webhook URL は「知っていれば認証なしで当該チャンネルへ投稿できる」＝それ自体が資格情報である。
// IHttpClientFactory の既定ログはリクエスト URI を平文で出力し（カテゴリ System.Net.Http.HttpClient.<name>.*）、
// otel 経由で Loki に蓄積されるため、**このクライアントに限って**既定ログを外し、URL を伏せた logger に差し替える。
//
// 抑止は名前付きクライアント単位で閉じる。risk-kill-switch / risk-pause / risk-stage-gate / discord-owner-token の
// 既定リクエストログは変更しない（可観測性を落とさない）。
public static class DiscordWebhookHttpClientExtensions
{
    public const string ClientName = "discord";

    public static IServiceCollection AddDiscordWebhookHttpClient(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(ClientName)
            .RemoveAllLoggers()
            .AddLogger(sp => new RedactedUriHttpClientLogger(
                sp.GetRequiredService<ILogger<RedactedUriHttpClientLogger>>()));

        return services;
    }
}
