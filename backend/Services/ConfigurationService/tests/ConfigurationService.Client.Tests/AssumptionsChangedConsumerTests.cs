using AiStockTrading.Configuration.Client.Composable.Steps;
using AiStockTrading.Configuration.Client.Ports;
using AiStockTrading.Shared.Contracts.Events;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.Configuration.Client.Tests;

// FR-17, UC-06, IADR-0060 決定 1/4: 利用者の前提条件変更（AssumptionsChanged）でキャッシュが無効化され、次の参照で
// 新しい版へ追随することを検証する（#139 の受け入れ基準「版が上がったときに追随する」）。
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
        await using var provider = new ServiceCollection()
            .AddSingleton<IAssumptionsCacheInvalidator>(invalidator)
            .AddMassTransitTestHarness(x => x.AddConsumer<AssumptionsChangedConsumer>())
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new AssumptionsChanged(
            Version: 4, Actor: "owner", Reason: "月次費用上限の引き下げ", ChangedAt: DateTimeOffset.UtcNow));

        (await harness.Consumed.Any<AssumptionsChanged>()).Should().BeTrue();
        invalidator.InvalidateCount.Should().Be(1);
    }
}
