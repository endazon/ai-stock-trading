using AiStockTrading.CostControl.Application.Adapters;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.CostControl.Application.Tests;

// NFR（費用）, IADR-0055 決定5: 重複排除ストアの基本性質（初回のみ true・Unmark で再試行可能）。
public class InMemoryProcessedMessageStoreTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    [Fact]
    public void 初回は_true_二回目以降は_false_を返す()
    {
        var store = new InMemoryProcessedMessageStore();
        var id = Guid.NewGuid();

        store.TryMarkProcessed(id, At).Should().BeTrue();
        store.TryMarkProcessed(id, At).Should().BeFalse();
        store.TryMarkProcessed(id, At).Should().BeFalse();
    }

    [Fact]
    public void 別の_MessageId_は互いに影響しない()
    {
        var store = new InMemoryProcessedMessageStore();

        store.TryMarkProcessed(Guid.NewGuid(), At).Should().BeTrue();
        store.TryMarkProcessed(Guid.NewGuid(), At).Should().BeTrue();
    }

    [Fact]
    public void Unmark_すると再び処理できる_計上失敗時の再試行()
    {
        var store = new InMemoryProcessedMessageStore();
        var id = Guid.NewGuid();

        store.TryMarkProcessed(id, At).Should().BeTrue();
        store.Unmark(id);

        store.TryMarkProcessed(id, At).Should().BeTrue();
    }

    [Fact]
    public void 未記録の_Unmark_は無害()
    {
        var store = new InMemoryProcessedMessageStore();

        var act = () => store.Unmark(Guid.NewGuid());

        act.Should().NotThrow();
    }
}
