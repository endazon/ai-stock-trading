using System.Reflection;

namespace AiStockTrading.Shared.Contracts.Events;

// ADR-0001, FR-11, #22: ドメインイベント（Shared.Contracts.Events の record 型）の**単一の母集合**を提供する。
// 監査カバレッジ（AuditConsumerCoverageTests）と後方互換契約テスト（EventBackwardCompatibilityTests）が
// 同一の母集合を共有し、片方だけ条件を変えて対象がサイレントに乖離することを防ぐ（IADR-0079）。
public static class EventTypeDiscovery
{
    public const string EventsNamespace = "AiStockTrading.Shared.Contracts.Events";

    // Events 名前空間の全ドメインイベント record 型を返す。
    public static IReadOnlyList<Type> GetEventTypes() =>
        typeof(EventTypeDiscovery).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Namespace == EventsNamespace && IsRecord(t))
            .ToList();

    // record 型はコンパイラが値等価用の "<Clone>$" メソッドを生成する。これで record と通常 class を判別する。
    public static bool IsRecord(Type t) =>
        t.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null;
}
