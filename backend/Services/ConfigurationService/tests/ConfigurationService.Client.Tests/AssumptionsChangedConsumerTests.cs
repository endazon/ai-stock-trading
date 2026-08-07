using AiStockTrading.Configuration.Client.Composable.Steps;
using AiStockTrading.Configuration.Client.Ports;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace AiStockTrading.Configuration.Client.Tests;

// FR-17, UC-06, IADR-0063 決定 1/4: 利用者の前提条件変更（AssumptionsChanged）でキャッシュが無効化され、次の参照で
// 新しい版へ追随することを検証する（#139 の受け入れ基準「版が上がったときに追随する」）。
//
// ADR-0013, IADR-0129, #354: MassTransit のテストハーネス（AddMassTransitTestHarness + harness.Consumed）から
// Wolverine.Tracking（TrackActivity + session.Executed）へ移行した。表明の意味は同じ
// （メッセージを流し、ハンドラが実行され、キャッシュが 1 回無効化される）。
public class AssumptionsChangedConsumerTests
{
    private sealed class SpyInvalidator : IAssumptionsCacheInvalidator
    {
        public int InvalidateCount { get; private set; }

        public void Invalidate() => InvalidateCount++;
    }

    [Fact]
    public async Task 前提条件の変更でキャッシュを無効化する()
    {
        var invalidator = new SpyInvalidator();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IAssumptionsCacheInvalidator>(invalidator);
                opts.Discovery.IncludeAssembly(typeof(AssumptionsChangedHandler).Assembly);
                // 実ブローカへ接続しない（ローカル・CI ともに RabbitMQ を要求しない）。
                opts.StubAllExternalTransports();
            })
            .StartAsync();

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new AssumptionsChanged(
            Version: 4, Actor: "owner", Reason: "月次費用上限の引き下げ", ChangedAt: DateTimeOffset.UtcNow));

        session.Executed.MessagesOf<AssumptionsChanged>().Should().NotBeEmpty();
        invalidator.InvalidateCount.Should().Be(1);

        await host.StopAsync();
    }
}
