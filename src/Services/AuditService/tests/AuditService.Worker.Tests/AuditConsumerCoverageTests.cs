using AiStockTrading.Audit.Worker.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using FluentAssertions;
using MassTransit;
using Xunit;

namespace AiStockTrading.Audit.Worker.Tests;

// FR-11, #80: 「全イベントの時系列記録」の担保。Shared.Contracts.Events の全イベントに対応する監査 Consumer が
// 存在することをリフレクションで検証し、新規イベント追加時の監査購読の追随漏れを CI で検知する。
public class AuditConsumerCoverageTests
{
    [Fact]
    public void 全ドメインイベントに対応する監査コンシューマが存在する()
    {
        // Shared.Contracts.Events 名前空間の全イベント型（record class）。
        var eventTypes = typeof(InformationCollected).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Namespace == "AiStockTrading.Shared.Contracts.Events")
            .ToList();

        eventTypes.Should().NotBeEmpty();

        // Audit.Worker アセンブリで IConsumer<T> を実装している型が購読対象とする型の集合。
        var consumedTypes = typeof(PriceMovementDetectedAuditConsumer).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .SelectMany(t => t.GetInterfaces())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>))
            .Select(i => i.GetGenericArguments()[0])
            .ToHashSet();

        var missing = eventTypes.Where(e => !consumedTypes.Contains(e)).Select(e => e.Name).ToList();

        missing.Should().BeEmpty(
            "全ドメインイベントは監査台帳へ記録する（FR-11）。未購読のイベントは AuditEventConsumers に Consumer を追加すること");
    }
}
