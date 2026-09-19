using System.Text.Json;
using System.Text.Json.Nodes;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// FR-10, FR-12, ADR-0040 決定1・決定3, #819, IADR-0342 決定1・決定3・決定6: 損切りの実行機構を運ぶ契約の固定。
//
// EventBackwardCompatibilityTests はプロパティの型名しか見ない。本テストはその外側の 3 点を押さえる。
//   1. **序数**: 0 ＝ S0。旧いメッセージ・旧い設定行が「項目なし＝ 0 ＝ S0」として読まれることが後方互換の根拠である。
//   2. **項目を持たない旧い OrderApproved 本文が S0 として読める**（受け入れ基準「S0 既定で現行挙動が変わらない」）。
//   3. **免除の事実が JSON を往復しても変わらない**（監査台帳の payload が唯一の一次証跡になる）。
public class StopLossMethodContractTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 14, 0, 0, TimeSpan.Zero);

    private static OrderIntent EntryIntent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
            10, 1_000m, PositionEffect.Open, StopLossPrice: 950m, FxRateToBase: 1m);

    [Theory]
    [InlineData(StopLossExecutionMethod.BrokerStopOrder, 0)]
    [InlineData(StopLossExecutionMethod.SoftwareStop, 1)]
    [InlineData(StopLossExecutionMethod.NoProtectiveStop, 2)]
    [InlineData(StopLossExecutionMethod.AlternativeBrokerOrderType, 3)]
    public void 手法の序数は計画のS0からS3に一致し動かない(StopLossExecutionMethod method, int ordinal)
    {
        ((int)method).Should().Be(ordinal);
    }

    [Fact]
    public void 手法を明示しない承認はS0である()
    {
        new OrderApproved(Guid.NewGuid(), EntryIntent(), 10, T0).StopLossMethod
            .Should().Be(StopLossExecutionMethod.BrokerStopOrder);
    }

    // 🔴 旧版の発行側（本項目を持たない）が送った本文を新版の受け手が読む経路。
    [Fact]
    public void 手法を持たない旧い承認本文はS0として読める()
    {
        var current = new OrderApproved(
            Guid.NewGuid(), EntryIntent(), 10, T0, StopLossMethod: StopLossExecutionMethod.NoProtectiveStop);
        var node = JsonNode.Parse(JsonSerializer.Serialize(current))!.AsObject();
        node.Remove(nameof(OrderApproved.StopLossMethod)).Should().BeTrue("旧版の本文には本項目が無い");

        var restored = JsonSerializer.Deserialize<OrderApproved>(node.ToJsonString())!;

        restored.StopLossMethod.Should().Be(StopLossExecutionMethod.BrokerStopOrder);
        restored.DecisionId.Should().Be(current.DecisionId);
    }

    [Fact]
    public void 手法つきの承認はJSONを往復しても手法を保つ()
    {
        var approved = new OrderApproved(
            Guid.NewGuid(), EntryIntent(), 10, T0, StopLossMethod: StopLossExecutionMethod.NoProtectiveStop);

        JsonSerializer.Deserialize<OrderApproved>(JsonSerializer.Serialize(approved))!
            .StopLossMethod.Should().Be(StopLossExecutionMethod.NoProtectiveStop);
    }

    [Fact]
    public void 免除の事実はJSONを往復しても値が変わらない()
    {
        var evt = new ProtectiveStopWaived(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, 10, 950m,
            StopLossExecutionMethod.NoProtectiveStop, BrokerProvider.MoomooSimulate, T0);

        JsonSerializer.Deserialize<ProtectiveStopWaived>(JsonSerializer.Serialize(evt)).Should().Be(evt);
    }

    // FR-10, ADR-0040 決定1（S1）, #820, IADR-0344 決定8: S1 の配置と発動結果も監査 payload が一次証跡になる。
    [Fact]
    public void ソフトウェア逆指値の配置と発動結果はJSONを往復しても値が変わらない()
    {
        var armed = new SoftwareStopArmed(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, 10, 950m,
            BrokerProvider.MoomooSimulate, T0);
        JsonSerializer.Deserialize<SoftwareStopArmed>(JsonSerializer.Serialize(armed)).Should().Be(armed);

        var closeIntent = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 10, 940m, PositionEffect.Close, StopLossPrice: null, FxRateToBase: 1m);
        var executed = new SoftwareStopExecuted(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, SoftwareStopOutcome.ClosePlaced, 10, 950m, 940m, 1,
            Guid.NewGuid(), "close-1", closeIntent, T0);
        var restored = JsonSerializer.Deserialize<SoftwareStopExecuted>(JsonSerializer.Serialize(executed))!;
        restored.Should().BeEquivalentTo(executed);
    }

    [Fact]
    public void ソフトウェア逆指値の発動結果の序数は動かない()
    {
        ((int)SoftwareStopOutcome.ClosePlaced).Should().Be(0);
        ((int)SoftwareStopOutcome.EntryCancelled).Should().Be(1);
        ((int)SoftwareStopOutcome.CloseRejected).Should().Be(2);
        ((int)SoftwareStopOutcome.EntryMissing).Should().Be(3);
        // #820 の 4 巡目監査, IADR-0344 追記(4) 決定9: 末尾へ追加した（既存の序数を動かさない）。
        ((int)SoftwareStopOutcome.CloseStalled).Should().Be(4);
        // #820 の 5 巡目監査, IADR-0344 追記(5): 外部要因による保護対象の減少（末尾へ追加）。
        ((int)SoftwareStopOutcome.ProtectionReduced).Should().Be(5);
        // #820 の 8 巡目監査, IADR-0344 追記(8): 帳簿では守っているのに 1 株も動かせない状態（末尾へ追加）。
        ((int)SoftwareStopOutcome.ProtectionSuspended).Should().Be(6);
        // #820 の 10 巡目監査, IADR-0344 追記(9) 決定3: どの保護記録も主張していない建玉の検知（末尾へ追加）。
        ((int)SoftwareStopOutcome.UnattributedPosition).Should().Be(7);
        // 🔴 序数だけでなく**値の総数**も固定する（末尾追加なら 1 つ増える。既存値の削除・並べ替えを捕まえる）。
        Enum.GetValues<SoftwareStopOutcome>().Should().HaveCount(8);
    }

    // 見送りの理由は末尾追加であり、既存 3 値の序数を動かさない（メトリクスのタグ・監査 payload の整数）。
    [Fact]
    public void 手法による見送りの理由は末尾に追加され既存の序数を動かさない()
    {
        ((int)OrderDispatchForgoneReason.BrokerUnavailable).Should().Be(0);
        ((int)OrderDispatchForgoneReason.StopLossPriceMissing).Should().Be(1);
        ((int)OrderDispatchForgoneReason.StopOrderUnsupported).Should().Be(2);
        ((int)OrderDispatchForgoneReason.StopLossMethodNotPermitted).Should().Be(3);

        // 🔴 T-10-518, #864, IADR-0355: 決済をブローカーの実建玉と突き合わせて止めた 2 値も**末尾**である。
        // 序数はメトリクスのタグ・監査 payload の整数として往来するため、間に挿し込むと過去の記録の意味が変わる。
        ((int)OrderDispatchForgoneReason.BrokerPositionAbsent).Should().Be(4);
        ((int)OrderDispatchForgoneReason.BrokerPositionsIndeterminate).Should().Be(5);

        // #820 の 8 巡目監査, IADR-0344 追記(8) 決定4: 帰属不明の建玉がある銘柄では S1 を武装しない。
        // 🔴 #864 が序数 4・5 を先に取ったため 4 → 6 へ繰り下げた（先にマージされた側が確保する）。
        ((int)OrderDispatchForgoneReason.UnattributedPosition).Should().Be(6);

        // 値を増やしたら、見送りを分類し直す側（在庫解放の可否など）も引き直させる。
        Enum.GetValues<OrderDispatchForgoneReason>().Should().HaveCount(7);
    }
}
