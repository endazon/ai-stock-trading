using NotificationService.Domain;
using NotificationService.Features.Notifications;
using NotificationService.Infrastructure.Steps;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace NotificationService.Tests;

// FR-14, FR-20, FR-07, UC-06, #861, #868, IADR-0383 決定4:
// **多層認証の対応付けの値が代理（onBehalfOf）の値域を外れていたら、Bot の起動時に警告する。**
//
// 権威側（報告書サービス・リスク管理）は値域外の onBehalfOf を 400 で弾く。Discord は唯一の確定・承認の窓口で
// あるため、値域外の対応付けは**その利用者が恒常的に何も確定・承認できない**ことを意味する。実行時の 400 は
// 押した人にしか見えないので、設定を投入した時点で気付ける場所＝起動ログへ出す。
//
// テスト ID: T-139（`docs/tests/FR-20_staged-gates-tests.md`）。
public class DiscordBotUserMappingWarningTests
{
    private sealed class NoopGateway : IDiscordBotGateway
    {
        public int Starts { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Starts++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingLogger : ILogger<DiscordBotHostedService>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    private static DiscordBotOptions OptionsWith(params (string DiscordUserId, string KeycloakUser)[] mapping)
    {
        var options = new DiscordBotOptions { GuildId = "guild-1", ChannelId = "channel-1" };
        foreach (var (discordUserId, keycloakUser) in mapping)
        {
            options.AllowedUserIds.Add(discordUserId);
            options.UserMapping[discordUserId] = keycloakUser;
        }

        return options;
    }

    private static async Task<RecordingLogger> StartAsync(DiscordBotOptions options, NoopGateway gateway)
    {
        var logger = new RecordingLogger();
        await new DiscordBotHostedService(gateway, options, logger).StartAsync(CancellationToken.None);
        return logger;
    }

    [Theory]
    [InlineData("山田")]        // #861 の監査が稼働環境で実測（Rejected）
    [InlineData("dev owner")]   // 同上
    [InlineData("owner\n@everyone")]
    public async Task T139_値域外の対応付けは起動時に警告する(string keycloakUser)
    {
        var gateway = new NoopGateway();

        var logger = await StartAsync(OptionsWith(("discord-1", keycloakUser)), gateway);

        logger.Warnings.Should().ContainSingle()
            .Which.Should().Contain("discord-1").And.Contain("値域");
        // 🔴 **起動は止めない**（他の利用者の kill switch まで止めない）。
        gateway.Starts.Should().Be(1);
    }

    [Fact]
    public async Task T139_値域内の対応付けでは警告を出さない()
    {
        var logger = await StartAsync(
            OptionsWith(("discord-1", "endazon"), ("discord-2", "first.last@example.com")), new NoopGateway());

        logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task T139_対応付けが空でも警告を出さない()
    {
        // 対応付けが無いこと自体は多層認証（層5）が全拒否で扱う。ここで二重に鳴らさない。
        var logger = await StartAsync(OptionsWith(), new NoopGateway());

        logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task T139_値域外が複数あればすべて挙げる()
    {
        // **1 件目で打ち切らない。** 直せるのは挙がったものだけである。
        var logger = await StartAsync(
            OptionsWith(("discord-1", "山田"), ("discord-2", "endazon"), ("discord-3", "dev owner")),
            new NoopGateway());

        logger.Warnings.Should().HaveCount(2);
        logger.Warnings.Should().Contain(w => w.Contains("discord-1", StringComparison.Ordinal));
        logger.Warnings.Should().Contain(w => w.Contains("discord-3", StringComparison.Ordinal));
        logger.Warnings.Should().NotContain(w => w.Contains("discord-2", StringComparison.Ordinal));
    }
}
