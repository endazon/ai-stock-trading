using AiStockTrading.Notification.Application.Services;
using AiStockTrading.Notification.Application.State;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.Notification.Application.Tests;

// FR-14, UC-06, IADR-0063 決定3: 多層認証（詳細設計07「認証・認可（本人確認）」）。
// 受け入れ基準 1〜5: DM 拒否・専用サーバー/チャンネル限定・許可リスト・設定が空なら全拒否・Keycloak マッピング必須。
public class DiscordCommandAuthorizerTests
{
    private const string Guild = "guild-1";
    private const string Channel = "channel-1";
    private const string OwnerUser = "discord-owner-1";
    private const string Actor = "endazon";

    // 全層を満たす正常な設定。各テストで1層だけ崩して拒否を確認する。
    private static DiscordBotOptions FullyConfigured()
    {
        var options = new DiscordBotOptions
        {
            GuildId = Guild,
            ChannelId = Channel,
            KillSwitchConfirmationPhrase = "STOP TRADING",
        };
        options.AllowedUserIds.Add(OwnerUser);
        options.UserMapping[OwnerUser] = Actor;
        return options;
    }

    private static DiscordCommandContext Context(
        string? guild = Guild,
        string? channel = Channel,
        string? user = OwnerUser,
        bool isDm = false) =>
        new(guild, channel, user, isDm, "/killswitch");

    [Fact]
    public void 全層を満たす本人の操作は許可され_Keycloak利用者名がactorになる()
    {
        var result = DiscordCommandAuthorizer.Authorize(Context(), FullyConfigured());

        result.IsAllowed.Should().BeTrue();
        result.Actor.Should().Be(Actor);
    }

    // 受け入れ基準1: 詳細設計07「DM は不使用」。DM は他の層を満たしていても拒否する。
    [Fact]
    public void DM_からの操作は拒否される()
    {
        var result = DiscordCommandAuthorizer.Authorize(Context(isDm: true), FullyConfigured());

        result.IsAllowed.Should().BeFalse();
        result.Actor.Should().BeNull();
        result.Reason.Should().Contain("DM");
    }

    // 受け入れ基準2: 専用サーバー・チャンネル限定。
    [Fact]
    public void 専用サーバー以外からの着信は拒否される()
    {
        var result = DiscordCommandAuthorizer.Authorize(Context(guild: "other-guild"), FullyConfigured());

        result.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void 専用チャンネル以外からの着信は拒否される()
    {
        var result = DiscordCommandAuthorizer.Authorize(Context(channel: "other-channel"), FullyConfigured());

        result.IsAllowed.Should().BeFalse();
    }

    // 受け入れ基準3: 「本人以外は操作できない」（計画の受け入れ基準）。
    [Fact]
    public void 許可リスト外のユーザーは操作できない()
    {
        var result = DiscordCommandAuthorizer.Authorize(Context(user: "intruder"), FullyConfigured());

        result.IsAllowed.Should().BeFalse();
        result.Actor.Should().BeNull();
    }

    // 受け入れ基準4（本仕様の要）: 設定が空でも「全許可」にならない＝ fail-safe。
    [Fact]
    public void GuildId_が未設定なら全拒否する()
    {
        var options = FullyConfigured();
        options.GuildId = null;

        DiscordCommandAuthorizer.Authorize(Context(), options).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void ChannelId_が未設定なら全拒否する()
    {
        var options = FullyConfigured();
        options.ChannelId = null;

        DiscordCommandAuthorizer.Authorize(Context(), options).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void 許可ユーザーIDが空なら全拒否する()
    {
        var options = FullyConfigured();
        options.AllowedUserIds.Clear();

        DiscordCommandAuthorizer.Authorize(Context(), options).IsAllowed.Should().BeFalse();
    }

    // 既定の DiscordBotOptions（何も設定しない）＝全拒否。設定漏れが全開放にならないことを固定する。
    [Fact]
    public void 既定の設定は全拒否である()
    {
        DiscordCommandAuthorizer.Authorize(Context(), new DiscordBotOptions()).IsAllowed.Should().BeFalse();
    }

    // 受け入れ基準5: actor を特定できない操作はさせない（監査ログに本人として残せないため）。
    [Fact]
    public void Keycloak_マッピングが無いユーザーは拒否される()
    {
        var options = FullyConfigured();
        options.UserMapping.Clear();

        var result = DiscordCommandAuthorizer.Authorize(Context(), options);

        result.IsAllowed.Should().BeFalse();
        result.Reason.Should().Contain("actor");
    }

    [Fact]
    public void ユーザーIDを特定できない着信は拒否される()
    {
        DiscordCommandAuthorizer.Authorize(Context(user: null), FullyConfigured()).IsAllowed.Should().BeFalse();
    }

    // ユーザーIDは Discord のスノーフレークであり厳密一致で扱う（前方一致等の緩い照合をしない）。
    [Fact]
    public void 許可ユーザーIDの部分一致では許可しない()
    {
        DiscordCommandAuthorizer.Authorize(Context(user: OwnerUser + "9"), FullyConfigured())
            .IsAllowed.Should().BeFalse();
    }
}
