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
        // Shared.Contracts.Events 名前空間の全イベント型（record のみ）。将来この名前空間へイベント以外の
        // 補助クラス（static/マーカー等）が追加されても誤検知しないよう、record 型（コンパイラ生成の "<Clone>$" を持つ）に限定する。
        var eventTypes = typeof(InformationCollected).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Namespace == "AiStockTrading.Shared.Contracts.Events" && IsRecord(t))
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

    // record 型はコンパイラが値等価用の "<Clone>$" メソッドを生成する。これで record と通常 class を判別する。
    private static bool IsRecord(Type t) =>
        t.GetMethod("<Clone>$", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) is not null;
}
