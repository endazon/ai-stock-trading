using System.Reflection;
using AiStockTrading.Shared.Contracts.Events;
using FluentAssertions;
using MassTransit;
using Xunit;
using Xunit.Sdk;

namespace AiStockTrading.Shared.Contracts.Tests
{
    // NFR, ADR-0001, IADR-0037 / IADR-0079, Issue #253:
    // イベントの MassTransit 正準 URN を固定する回帰ガード。
    //
    // MassTransit はメッセージの wire 上の識別を正準 URN
    // （urn:message:<Namespace>:<TypeName>）で行う。URN は **名前空間と型名の双方**から導出されるため、
    // 名前空間の移動は型名が不変でも wire 契約を破壊し、キューに滞留中／_error キュー内のメッセージが
    // 再消費不能になる。
    //
    // IADR-0079 の EventBackwardCompatibilityTests は snapshot のキーを Type.Name（単純型名）で構成するため、
    // プロパティ・型名の破壊的変更は検出するが **名前空間の移動は検出しない**。本テストがその分担を担う
    // （IADR-0037 の決定「platform EventMessageUrnTests と同型の URN 回帰ガードを実装する」の遂行）。
    //
    // ⚠️ 本テストは MassTransit の URN 導出規約を前提とする。ADR-0013（Wolverine 移行への追随）が実施される際は、
    // 本テストを Wolverine の識別子規約へ更新するか、移行後の識別子固定テストへ置き換える必要がある。
    public class EventMessageUrnTests
    {
        // 期待値は **文字列リテラルで直書き**する。テスト側で eventType.Namespace から組み立てると、
        // 名前空間が動いたときに期待値も一緒に動いてしまい、ガードとして機能しない（トートロジー）。
        [Theory]
        [InlineData(typeof(AssumptionsChanged), "urn:message:AiStockTrading.Shared.Contracts.Events:AssumptionsChanged")]
        [InlineData(typeof(BacktestEvaluated), "urn:message:AiStockTrading.Shared.Contracts.Events:BacktestEvaluated")]
        [InlineData(typeof(CostThresholdReached), "urn:message:AiStockTrading.Shared.Contracts.Events:CostThresholdReached")]
        [InlineData(typeof(DailyPolicyUnconfirmed), "urn:message:AiStockTrading.Shared.Contracts.Events:DailyPolicyUnconfirmed")]
        [InlineData(typeof(InformationCollected), "urn:message:AiStockTrading.Shared.Contracts.Events:InformationCollected")]
        [InlineData(typeof(LlmCostIncurred), "urn:message:AiStockTrading.Shared.Contracts.Events:LlmCostIncurred")]
        [InlineData(typeof(OrderApproved), "urn:message:AiStockTrading.Shared.Contracts.Events:OrderApproved")]
        [InlineData(typeof(OrderCancelled), "urn:message:AiStockTrading.Shared.Contracts.Events:OrderCancelled")]
        [InlineData(typeof(OrderExecuted), "urn:message:AiStockTrading.Shared.Contracts.Events:OrderExecuted")]
        [InlineData(typeof(OrderModified), "urn:message:AiStockTrading.Shared.Contracts.Events:OrderModified")]
        [InlineData(typeof(OrderRejected), "urn:message:AiStockTrading.Shared.Contracts.Events:OrderRejected")]
        [InlineData(typeof(PriceMovementDetected), "urn:message:AiStockTrading.Shared.Contracts.Events:PriceMovementDetected")]
        [InlineData(typeof(ReportConfirmed), "urn:message:AiStockTrading.Shared.Contracts.Events:ReportConfirmed")]
        [InlineData(typeof(ReportDraftPresented), "urn:message:AiStockTrading.Shared.Contracts.Events:ReportDraftPresented")]
        [InlineData(typeof(StageTransitioned), "urn:message:AiStockTrading.Shared.Contracts.Events:StageTransitioned")]
        [InlineData(typeof(StopLossTriggered), "urn:message:AiStockTrading.Shared.Contracts.Events:StopLossTriggered")]
        [InlineData(typeof(TradeDecisionMade), "urn:message:AiStockTrading.Shared.Contracts.Events:TradeDecisionMade")]
        [InlineData(typeof(WithdrawalTriggered), "urn:message:AiStockTrading.Shared.Contracts.Events:WithdrawalTriggered")]
        public void 全イベントの正準URNは固定値である(Type eventType, string expectedUrn)
        {
            // MessageUrn.ForType を実際に呼ぶ（URN 文字列を自前で組み立てない）。
            // MassTransit の URN 導出規約が将来バージョンで変わった場合も、本テストが赤になって検知できる。
            MessageUrn.ForType(eventType).ToString().Should().Be(expectedUrn);
        }

        // 固定漏れ・固定残りを防ぐ。母集合は IADR-0079 の後方互換テスト・監査カバレッジテストと同じ
        // EventTypeDiscovery に単一化し、対象がサイレントに乖離しないようにする。
        [Fact]
        public void URN固定の対象はイベント型の母集合と完全に一致する()
        {
            var pinned = PinnedEventTypes();
            var discovered = EventTypeDiscovery.GetEventTypes();

            pinned.Should().BeEquivalentTo(discovered,
                "新イベントを追加したら正準 URN も固定すること（固定漏れ）。"
                + "イベントを削除したら固定も外すこと（固定残り）。"
                + $"固定={pinned.Count} 件 / 母集合={discovered.Count} 件");
        }

        // 本ガードが load-bearing であること（＝名前空間の移動を実際に検出できること）の構造的証明。
        // Issue #253 受け入れ基準②の「一時的な改名での赤確認」を、一過性の手作業ではなくテストとして残す。
        [Fact]
        public void 名前空間が変わればURNも変わる_本ガードが名前空間移動を検出できることの証明()
        {
            // MovedNamespaceProbe.TradeDecisionMade は本物と**同じ単純型名**を持ち、名前空間だけが違う。
            // IADR-0079 の Type.Name キーではこの 2 つは区別できない（＝名前空間移動を検出できない）が、
            // 正準 URN は異なる。
            var real = MessageUrn.ForType(typeof(TradeDecisionMade)).ToString();
            var moved = MessageUrn.ForType(typeof(MovedNamespaceProbe.TradeDecisionMade)).ToString();

            typeof(MovedNamespaceProbe.TradeDecisionMade).Name.Should().Be(typeof(TradeDecisionMade).Name);
            moved.Should().NotBe(real);
            moved.Should().Be(
                "urn:message:AiStockTrading.Shared.Contracts.Tests.MovedNamespaceProbe:TradeDecisionMade");
        }

        // 固定済みの型集合を [InlineData] 属性そのものから取り出す（固定の単一情報源は上の Theory）。
        private static IReadOnlyList<Type> PinnedEventTypes()
        {
            var method = typeof(EventMessageUrnTests)
                .GetMethod(nameof(全イベントの正準URNは固定値である), BindingFlags.Public | BindingFlags.Instance)!;

            return method.GetCustomAttributes<InlineDataAttribute>()
                .SelectMany(a => a.GetData(method))
                .Select(row => (Type)row[0]!)
                .ToList();
        }
    }

    // 名前空間移動を模したプローブ。イベントと同名だが名前空間が異なる＝正準 URN が異なる。
    // Shared.Contracts アセンブリの外（テストアセンブリ）に置くため、EventTypeDiscovery の母集合には入らない。
    namespace MovedNamespaceProbe
    {
        public sealed record TradeDecisionMade(Guid DecisionId);
    }
}
