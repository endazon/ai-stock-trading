using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using TradeDecisionService.Infrastructure.Steps;
using Xunit;

namespace TradeDecisionService.Tests;

// 🔴 T-10-3077, FR-02, NFR-02, #1485, IADR-0593 決定2: 定時判断の発行口は、送れた決定的な DecisionId だけを覚え（送信の失敗・決定的でない ID は覚えない）、
// 覚える数は上限で古いものから忘れる。
public class ScheduledDecisionPublisherTests
{
    private static TradeDecisionMade Decision(Guid id) =>
        new(id,
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
                1, 100m, PositionEffect.Open, StopLossPrice: 95m),
            "判断", DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task T_10_3077_送れた決定的なDecisionIdだけを覚える()
    {
        var sent = new List<Guid>();
        var publisher = new ScheduledDecisionPublisher(d =>
        {
            sent.Add(d.DecisionId);
            return ValueTask.CompletedTask;
        });
        var remembered = Guid.NewGuid();
        var notRemembered = Guid.NewGuid();

        await publisher.PublishAsync(Decision(remembered), remember: true);
        await publisher.PublishAsync(Decision(notRemembered), remember: false);

        sent.Should().Equal(remembered, notRemembered);
        publisher.AlreadyPublished(remembered).Should().BeTrue();
        publisher.AlreadyPublished(notRemembered).Should().BeFalse("決定的でない DecisionId は覚えない（再試行で判断し直す）");
    }

    [Fact]
    public async Task T_10_3077_送信に失敗した判断は覚えない()
    {
        var publisher = new ScheduledDecisionPublisher(_ => ValueTask.FromException(new InvalidOperationException("送信の失敗")));
        var id = Guid.NewGuid();

        var act = () => publisher.PublishAsync(Decision(id), remember: true);

        await act.Should().ThrowAsync<InvalidOperationException>("送信の例外は銘柄ごとの catch へ伝える");
        publisher.AlreadyPublished(id).Should().BeFalse("送れていない判断は次の試行で判断し直す");
    }

    [Fact]
    public async Task T_10_3077_覚える数は上限で古いものから忘れる()
    {
        var publisher = new ScheduledDecisionPublisher(_ => ValueTask.CompletedTask);
        var ids = Enumerable.Range(0, ScheduledDecisionPublisher.RememberedCapacity + 1).Select(_ => Guid.NewGuid()).ToList();

        foreach (var id in ids)
            await publisher.PublishAsync(Decision(id), remember: true);

        publisher.AlreadyPublished(ids[0]).Should().BeFalse("最も古いものから忘れる");
        publisher.AlreadyPublished(ids[1]).Should().BeTrue();
        publisher.AlreadyPublished(ids[^1]).Should().BeTrue();
    }
}
