using AiStockTrading.Audit.Infrastructure.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using MassTransit;
using Xunit;

namespace AiStockTrading.Audit.Infrastructure.Tests;

// FR-11, #80: 「全イベントの時系列記録」の担保。Shared.Contracts.Events の全イベントに対応する監査 Consumer が
// 存在することをリフレクションで検証し、新規イベント追加時の監査購読の追随漏れを CI で検知する。
public class AuditConsumerCoverageTests
{
    [Fact]
    public void 全ドメインイベントに対応する監査コンシューマが存在する()
    {
        // Shared.Contracts.Events 名前空間の全イベント型（record のみ）。母集合は EventTypeDiscovery で単一化し、
        // 後方互換契約テスト（EventBackwardCompatibilityTests）と同一の対象を共有する（IADR-0079。片方だけ条件を
        // 変えて対象がサイレントに乖離するのを防ぐ）。static 補助クラス等は record 判定で自然に除外される。
        var eventTypes = EventTypeDiscovery.GetEventTypes();

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
