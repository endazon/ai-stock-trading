using AiStockTrading.Notification.Application.Ports;
using AiStockTrading.Notification.Application.Services;
using AiStockTrading.Notification.Application.State;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Notification.Infrastructure.Composable.Adapters;

// FR-14, IADR-0062 決定1: Bot Gateway の選択。安全既定は接続しない no-op（IADR-0020 の
// NotificationSenderFactory と同型）。実 Gateway 接続は設定が揃った時のみ。
//
// 有効化の条件（すべて満たす必要がある）:
//   1. Notifications:Discord:Bot:Enabled=true（明示的なオプトイン）
//   2. Token が設定済み
//   3. 多層認証の設定（GuildId / ChannelId / 許可ユーザー / Keycloak マッピング）が揃っている
//
// 3 が要点である。認証設定が欠けたまま Gateway に接続すると、着信は Authorizer が全拒否するため実害は
// 出ないが、「操作を受け付けない Bot がオンラインに見える」状態になる。設定不備は接続前に落とす方が明確で、
// 誤設定に気付ける（起動時ログ）。IADR-0016 の実弾防止ゲートと同じく、設定不備では動かさない。
internal static class DiscordBotGatewayFactory
{
    public static IDiscordBotGateway Create(
        DiscordBotOptions options,
        KillSwitchCommandHandler handler,
        PauseCommandHandler pauseHandler,
        StageGateCommandHandler stageGateHandler,
        // FR-19, #464, ADR-0028 決定3: GFV 違反による停止の解除（Discord Bot が唯一の窓口）。
        GoodFaithViolationCommandHandler goodFaithViolationHandler,
        // FR-07, FR-14, UC-03〜05, #341, IADR-0240: 報告書レビュー（版番号の照会・冪等確定・差し戻し）。
        ReportCommandHandler reportHandler,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(DiscordBotGatewayFactory).FullName!);

        if (!options.Enabled)
            return NoOp(loggerFactory);

        if (string.IsNullOrWhiteSpace(options.Token))
        {
            logger.LogWarning(
                "Notifications:Discord:Bot:Enabled=true ですが Token が未設定のため Gateway に接続しません" +
                "（no-op へフォールバック・IADR-0062）。");
            return NoOp(loggerFactory);
        }

        // 多層認証の設定が1つでも欠ければ接続しない（設定漏れの Bot をオンラインにしない）。
        var missing = MissingAuthSettings(options);
        if (missing.Count > 0)
        {
            logger.LogWarning(
                "Discord Bot の多層認証の設定が不足しているため Gateway に接続しません（不足: {Missing}・IADR-0062）。",
                string.Join(", ", missing));
            return NoOp(loggerFactory);
        }

        return new DiscordNetBotGateway(
            handler, pauseHandler, stageGateHandler, goodFaithViolationHandler, reportHandler, options,
            loggerFactory.CreateLogger<DiscordNetBotGateway>());
    }

    // 不足している認証設定の一覧（運用者が起動時ログで欠落を特定できるようにする）。
    private static List<string> MissingAuthSettings(DiscordBotOptions options)
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(options.GuildId))
            missing.Add("GuildId");

        if (string.IsNullOrWhiteSpace(options.ChannelId))
            missing.Add("ChannelId");

        if (options.AllowedUserIds.Count == 0)
            missing.Add("AllowedUserIds");

        // 許可ユーザー全員に Keycloak 利用者の対応付けが要る（対応が無いユーザーは操作できないため）。
        if (options.AllowedUserIds.Count > 0
            && options.AllowedUserIds.Any(id => !options.UserMapping.ContainsKey(id)))
            missing.Add("UserMapping");

        // kill switch の閂。未設定なら起動できないため、接続前に落とす。
        if (string.IsNullOrWhiteSpace(options.KillSwitchConfirmationPhrase))
            missing.Add("KillSwitchConfirmationPhrase");

        return missing;
    }

    private static NullDiscordBotGateway NoOp(ILoggerFactory loggerFactory) =>
        new(loggerFactory.CreateLogger<NullDiscordBotGateway>());
}
