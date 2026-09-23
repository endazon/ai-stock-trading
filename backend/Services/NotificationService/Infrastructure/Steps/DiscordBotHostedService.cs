using AiStockTrading.Shared.Contracts.Logging;
using NotificationService.Domain;
using NotificationService.Features.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NotificationService.Infrastructure.Steps;

// FR-14, IADR-0062: Bot Gateway の常駐をホストのライフサイクルへ結ぶ。
// 既定の実装は NullDiscordBotGateway（接続しない）のため、本サービスは既定では接続を行わない。
//
// 接続失敗で host を落とさない: 通知（FR-09 アウトバウンド）は Bot と独立して動作すべきであり、
// Discord 障害で通知サービス全体が停止すると可用性 NFR に反する（詳細設計07「Discord 自体の障害への依存」）。
internal sealed class DiscordBotHostedService(
    IDiscordBotGateway gateway,
    DiscordBotOptions options,
    ILogger<DiscordBotHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        WarnOnOutOfRangeUserMapping();

        try
        {
            await gateway.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Discord Bot の起動に失敗しました（通知の送信は継続します）。");
        }
    }

    // FR-14, FR-20, FR-07, UC-06, #861, #868, IADR-0383 決定4:
    // **多層認証の対応付けの値が代理の値域を外れていたら、起動時に警告する。**
    //
    // 権威側（報告書サービス・リスク管理）は値域外の `onBehalfOf` を 400 で弾く。Discord は唯一の確定・承認の
    // 窓口であるため、値域外の対応付けは「その利用者は**恒常的に**報告書を確定できず段階も動かせない」を意味する
    // （#861 の監査が稼働環境で `'山田'` / `'dev owner'` の Rejected を実測した）。実行時の 400 は押した人にしか
    // 見えないので、**設定を投入した時点で気付ける場所**＝起動ログへ出す。
    //
    // 🔴 **起動を止めない。** 対応付けが 1 件でも値域外だと Bot ごと落とす形にすると、他の利用者の kill switch
    // まで止まる（安全既定は「接続しない」ではなく「操作を通さない」側で既に効いている——値域外の利用者の
    // 確定・承認は権威側が 400 で止める）。
    private void WarnOnOutOfRangeUserMapping()
    {
        var offenders = DelegatedActorName.OutOfRangeMappings(options.UserMapping);
        if (offenders.Count == 0)
            return;

        foreach (var (discordUserId, keycloakUser) in offenders)
        {
            logger.LogWarning(
                "Discord Bot の利用者対応付けの値が代理（onBehalfOf）の値域を外れています"
                + "（DiscordUserId={DiscordUserId}・KeycloakUser={KeycloakUser}・値域は {Range}）。"
                + "この利用者からの報告書の確定・段階遷移は権威側で 400 となり、恒常的に失敗します。",
                LogSanitizer.Sanitize(discordUserId),
                LogSanitizer.Sanitize(keycloakUser),
                DelegatedActorName.RangeDescription);
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
