using System.Reflection;
using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using Wolverine.Util;
using Xunit;
using Xunit.Sdk;

namespace AiStockTrading.Shared.Contracts.Tests
{
    // NFR, ADR-0013, IADR-0037 / IADR-0079 / IADR-0129, Issue #253 / #354:
    // イベントの **Wolverine メッセージ識別子** を固定する回帰ガード。
    //
    // ── 置き換えの経緯（本テストは EventMessageUrnTests の後継である）
    // 本テストの前身は `EventMessageUrnTests`（#253・IADR-0037）であり、MassTransit の正準 URN
    // （`urn:message:<Namespace>:<TypeName>`）を 21 イベント分固定していた。#354（ADR-0013・Wolverine 移行）で
    // 本ユニットのメッセージングは Wolverine へ全面移行し、**MassTransit の URN は wire 上のどこにも現れなくなった**。
    // 前身テストは自ら「Wolverine 移行時は識別子規約へ更新するか置き換えが必要」と明記しており、その指示に従って
    // 第 3 段階（#354）で本テストへ置き換えた。旧テストを残せば「通っているのに何も守っていないガード」になる。
    //
    // ── 何を守るのか（表明の意図は前身から不変）
    // 「**メッセージ識別子が意図せず変わらないこと**」。識別子はメッセージの wire 契約であり、
    // 変わると次が起こる:
    //   - 発行側は新しい名前の exchange へ送り、購読側は古い名前のキュー／binding で待つ（互いに出会わない）
    //   - キューに滞留中のメッセージ・`<queue>_error`（DLQ）に退避済みのメッセージが**再消費不能**になる
    // Wolverine の識別子は `messageType.ToMessageTypeName()`（既定は namespace 込みの完全名）であり、
    // **exchange 名・binding key・封筒の `message-type` ヘッダ**がこの文字列である（IADR-0129 決定 2）。
    // したがって守る対象（＝名前空間の移動・型名の変更を検出すること）は前身とまったく同じで、
    // 固定する文字列だけが URN から Wolverine 識別子へ入れ替わった。
    //
    // ── 他テストとの分担
    // IADR-0079 の EventBackwardCompatibilityTests は snapshot のキーを Type.Name（単純型名）で構成するため、
    // プロパティ・型名の破壊的変更は検出するが **名前空間の移動は検出しない**。本テストがその分担を担う
    // （IADR-0037 の決定「URN 回帰ガードを実装する」の、Wolverine 世代における遂行）。
    // なお **キュー名**の一意性（#258・IADR-0106 → IADR-0129 決定 1）は識別子とは別の関心事であり、
    // 各サービスの `ConsumerEndpointNameTests` と `scripts/check-consumer-endpoint-names.js` が担う。
    public class EventMessageTypeNameTests
    {
        // 期待値は **文字列リテラルで直書き**する。テスト側で eventType.FullName から組み立てると、
        // 名前空間が動いたときに期待値も一緒に動いてしまい、ガードとして機能しない（トートロジー）。
        [Theory]
        [InlineData(typeof(AssumptionsChanged), "AiStockTrading.Shared.Contracts.Events.AssumptionsChanged")]
        [InlineData(typeof(BacktestEvaluated), "AiStockTrading.Shared.Contracts.Events.BacktestEvaluated")]
        // FR-19, #375, ADR-0021 決定3, IADR-0153: 口座種別の観測。
        [InlineData(typeof(BrokerAccountObserved), "AiStockTrading.Shared.Contracts.Events.BrokerAccountObserved")]
        [InlineData(typeof(BrokerAvailabilityObserved), "AiStockTrading.Shared.Contracts.Events.BrokerAvailabilityObserved")]
        [InlineData(typeof(BrokerPositionsObserved), "AiStockTrading.Shared.Contracts.Events.BrokerPositionsObserved")]
        // FR-10, FR-11, #419, ADR-0016 決定4（2026-08-06 改訂）, IADR-0159: 強制買戻しの事後推定。
        [InlineData(typeof(BuyInInferred), "AiStockTrading.Shared.Contracts.Events.BuyInInferred")]
        [InlineData(typeof(CostThresholdReached), "AiStockTrading.Shared.Contracts.Events.CostThresholdReached")]
        [InlineData(typeof(DailyPolicyUnconfirmed), "AiStockTrading.Shared.Contracts.Events.DailyPolicyUnconfirmed")]
        // FR-19, FR-11, #425, ADR-0025 決定2, IADR-0165: GFV 発生回数の自前計数。
        [InlineData(typeof(GoodFaithViolationRecorded), "AiStockTrading.Shared.Contracts.Events.GoodFaithViolationRecorded")]
        [InlineData(typeof(InformationCollected), "AiStockTrading.Shared.Contracts.Events.InformationCollected")]
        [InlineData(typeof(LlmCostIncurred), "AiStockTrading.Shared.Contracts.Events.LlmCostIncurred")]
        [InlineData(typeof(MaintenanceMarginReductionExecuted), "AiStockTrading.Shared.Contracts.Events.MaintenanceMarginReductionExecuted")]
        [InlineData(typeof(OrderApproved), "AiStockTrading.Shared.Contracts.Events.OrderApproved")]
        [InlineData(typeof(OrderCancelled), "AiStockTrading.Shared.Contracts.Events.OrderCancelled")]
        [InlineData(typeof(OrderExecuted), "AiStockTrading.Shared.Contracts.Events.OrderExecuted")]
        [InlineData(typeof(OrderModified), "AiStockTrading.Shared.Contracts.Events.OrderModified")]
        [InlineData(typeof(OrderRejected), "AiStockTrading.Shared.Contracts.Events.OrderRejected")]
        [InlineData(typeof(PositionCloseRequested), "AiStockTrading.Shared.Contracts.Events.PositionCloseRequested")]
        [InlineData(typeof(PositionReconciliationDrift), "AiStockTrading.Shared.Contracts.Events.PositionReconciliationDrift")]
        [InlineData(typeof(PriceMovementDetected), "AiStockTrading.Shared.Contracts.Events.PriceMovementDetected")]
        [InlineData(typeof(ReportConfirmed), "AiStockTrading.Shared.Contracts.Events.ReportConfirmed")]
        [InlineData(typeof(ReportDraftPresented), "AiStockTrading.Shared.Contracts.Events.ReportDraftPresented")]
        [InlineData(typeof(StageTransitioned), "AiStockTrading.Shared.Contracts.Events.StageTransitioned")]
        // #464, ADR-0028 決定2: GFV 違反による停止の解除（誰が・いつ・どの記録に対して）。
        [InlineData(typeof(GoodFaithViolationsCleared), "AiStockTrading.Shared.Contracts.Events.GoodFaithViolationsCleared")]
        // #465, ADR-0027 決定1/決定4: 借株料の日次の計上額と、**計上できなかった日**（別イベント）。
        [InlineData(typeof(BorrowFeeAccrued), "AiStockTrading.Shared.Contracts.Events.BorrowFeeAccrued")]
        [InlineData(typeof(BorrowFeeAccrualUnavailable), "AiStockTrading.Shared.Contracts.Events.BorrowFeeAccrualUnavailable")]
        // FR-10, FR-17, #381, ADR-0022 決定2・決定5, IADR-0196: 為替の情報源の劣化（切替・復帰・鮮度警告）。
        [InlineData(typeof(FxRateSourceFellBack), "AiStockTrading.Shared.Contracts.Events.FxRateSourceFellBack")]
        [InlineData(typeof(FxRateSourcePrimaryRestored), "AiStockTrading.Shared.Contracts.Events.FxRateSourcePrimaryRestored")]
        [InlineData(typeof(FxRateStale), "AiStockTrading.Shared.Contracts.Events.FxRateStale")]
        // #381 停止側: 鮮度切れのレートで決済した事実（IADR-0198）。
        [InlineData(typeof(PositionClosedWithStaleFxRate), "AiStockTrading.Shared.Contracts.Events.PositionClosedWithStaleFxRate")]
        [InlineData(typeof(StopLossTriggered), "AiStockTrading.Shared.Contracts.Events.StopLossTriggered")]
        [InlineData(typeof(TradeDecisionMade), "AiStockTrading.Shared.Contracts.Events.TradeDecisionMade")]
        [InlineData(typeof(WithdrawalTriggered), "AiStockTrading.Shared.Contracts.Events.WithdrawalTriggered")]
        // FR-01, #336, ADR-0020 決定2〜4: 情報源の欠測による縮退・回復と、一般 Web 収集の発動／解除。
        [InlineData(typeof(InformationSourceDegraded), "AiStockTrading.Shared.Contracts.Events.InformationSourceDegraded")]
        [InlineData(typeof(InformationSourceRecovered), "AiStockTrading.Shared.Contracts.Events.InformationSourceRecovered")]
        [InlineData(typeof(GeneralWebCollectionStateChanged), "AiStockTrading.Shared.Contracts.Events.GeneralWebCollectionStateChanged")]
        public void 全イベントのメッセージ識別子は固定値である(Type eventType, string expectedMessageTypeName)
        {
            // ToMessageTypeName を実際に呼ぶ（識別子を自前で組み立てない）。
            // Wolverine の導出規約が将来バージョンで変わった場合も、本テストが赤になって検知できる。
            eventType.ToMessageTypeName().Should().Be(expectedMessageTypeName);
        }

        // 固定漏れ・固定残りを防ぐ。母集合は IADR-0079 の後方互換テスト・監査カバレッジテストと同じ
        // EventTypeDiscovery に単一化し、対象がサイレントに乖離しないようにする。
        [Fact]
        public void 識別子固定の対象はイベント型の母集合と完全に一致する()
        {
            var pinned = PinnedEventTypes();
            var discovered = EventTypeDiscovery.GetEventTypes();

            pinned.Should().BeEquivalentTo(discovered,
                "新イベントを追加したらメッセージ識別子も固定すること（固定漏れ）。"
                + "イベントを削除したら固定も外すこと（固定残り）。"
                + $"固定={pinned.Count} 件 / 母集合={discovered.Count} 件");
        }

        // 本ガードが load-bearing であること（＝名前空間の移動を実際に検出できること）の構造的証明。
        // Issue #253 受け入れ基準②の「一時的な改名での赤確認」を、一過性の手作業ではなくテストとして残す
        // （前身 EventMessageUrnTests から引き継いだ表明。固定する文字列だけが Wolverine 識別子へ変わった）。
        [Fact]
        public void 名前空間が変われば識別子も変わる_本ガードが名前空間移動を検出できることの証明()
        {
            // MovedNamespaceProbe.TradeDecisionMade は本物と**同じ単純型名**を持ち、名前空間だけが違う。
            // IADR-0079 の Type.Name キーではこの 2 つは区別できない（＝名前空間移動を検出できない）が、
            // Wolverine の識別子は完全名であるため異なる。
            var real = typeof(TradeDecisionMade).ToMessageTypeName();
            var moved = typeof(MovedNamespaceProbe.TradeDecisionMade).ToMessageTypeName();

            typeof(MovedNamespaceProbe.TradeDecisionMade).Name.Should().Be(typeof(TradeDecisionMade).Name);
            moved.Should().NotBe(real);
            moved.Should().Be(
                "AiStockTrading.Shared.Contracts.Tests.MovedNamespaceProbe.TradeDecisionMade");
        }

        // 固定済みの型集合を [InlineData] 属性そのものから取り出す（固定の単一情報源は上の Theory）。
        private static IReadOnlyList<Type> PinnedEventTypes()
        {
            var method = typeof(EventMessageTypeNameTests)
                .GetMethod(nameof(全イベントのメッセージ識別子は固定値である), BindingFlags.Public | BindingFlags.Instance)!;

            // NFR, IADR-0001, #352: xUnit v3 の DataAttribute.GetData は
            // `ValueTask<IReadOnlyCollection<ITheoryDataRow>> GetData(MethodInfo, DisposalTracker)` へ変わった。
            // InlineData は 1 属性 = 1 行であり、v3 が公開する Data プロパティ（属性の実引数）から
            // 直接取り出せば非同期 API を呼ばずに済む（取り出す値は v2 と同一）。
            return method.GetCustomAttributes<InlineDataAttribute>()
                .Select(a => (Type)a.Data[0]!)
                .ToList();
        }
    }

    // 名前空間移動を模したプローブ。イベントと同名だが名前空間が異なる＝メッセージ識別子が異なる。
    // Shared.Contracts アセンブリの外（テストアセンブリ）に置くため、EventTypeDiscovery の母集合には入らない。
    namespace MovedNamespaceProbe
    {
        public sealed record TradeDecisionMade(Guid DecisionId);
    }
}
