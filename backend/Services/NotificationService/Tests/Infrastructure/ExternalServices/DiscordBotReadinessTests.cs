using System.Net;
using AiStockTrading.Shared.Contracts.Observability;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NotificationService.Infrastructure.ExternalServices;
using Wolverine;
using Xunit;

namespace NotificationService.Tests;

// 🔴 NFR, FR-14, IADR-0645 決定 3（AC4-1・AC4-2）: ボットが有効なのに設定が欠けて接続しないとき、
//   段 Enforce では readiness を落とす（オーナーの帯域内の停止の経路が無いまま入れ替えを進めない）。
//   陰性対照: 段 Report（既定）・ボットが無効（明示の選択）では落とさない。実 Discord へは繋がない（Token が無いので接続しない）。
public class DiscordBotReadinessTests
{
    private sealed class Factory(Dictionary<string, string?> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // Program.cs は構成を組み立ての時点で読む（ボットの設定・段）。UseSetting はその時点で見える。
            foreach (var (k, v) in settings)
                builder.UseSetting(k, v);
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:ConnectionString"] = "amqp://localhost",
                ["Otlp:Endpoint"] = "http://localhost:4317",
            }));
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }

    private static async Task<(HttpStatusCode Ready, string State)> ReadyAsync(bool enabled, string? mode)
    {
        var settings = new Dictionary<string, string?> { ["Notifications:Discord:Bot:Enabled"] = enabled ? "true" : "false" };
        if (mode is not null)
            settings[DiscordBotReadinessCheck.ModeKey] = mode;
        await using var factory = new Factory(settings);
        using var client = factory.CreateClient();
        var res = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        return (res.StatusCode, factory.Services.GetRequiredService<DiscordBotHealth>().State);
    }

    [Fact]
    public async Task 段Enforceで有効なのに設定が欠ければreadinessを落とす()
    {
        var (ready, state) = await ReadyAsync(enabled: true, mode: "Enforce");

        state.Should().Be(BusinessMetrics.DiscordBotMisconfigured);
        ready.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Theory]
    [InlineData(true, null, BusinessMetrics.DiscordBotMisconfigured)]
    [InlineData(true, "Report", BusinessMetrics.DiscordBotMisconfigured)]
    [InlineData(false, "Enforce", BusinessMetrics.DiscordBotDisabled)]
    public async Task 陰性対照_段Reportとボットが無効ではreadinessを落とさない(bool enabled, string? mode, string expectedState)
    {
        var (ready, state) = await ReadyAsync(enabled, mode);

        state.Should().Be(expectedState, "状態は段に依らず計器へ出る");
        ready.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void 知らない段は起動で止まる()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DiscordBotReadinessCheck.ModeKey] = "Strict" })
            .Build();

        var act = () => DiscordBotReadinessCheck.ReadMode(config);

        act.Should().Throw<InvalidOperationException>();
    }
}
