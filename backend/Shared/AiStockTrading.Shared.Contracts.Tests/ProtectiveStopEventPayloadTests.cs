using System.Text.Json;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// FR-05, FR-10, FR-11, #331, IADR-0210/0211: 損切りの逆指値一本化で足した 3 イベントと
// BrokerUnavailableException の**契約**を固定する。
//
// EventBackwardCompatibilityTests はプロパティの**型名**しか見ない（同テストの「既知の限界」）。
// 本テストはその外側にある 2 点を押さえる。
//   1. **JSON を往復しても値が変わらないこと。** 3 イベントはメッセージング（Wolverine）で
//      wire を渡り、監査台帳へ payload としてそのまま保存される。台帳の payload は
//      「なぜ発注されなかったか」「なぜ建玉が消えたか」の唯一の一次証跡であり、
//      往復で欠落・改変が起きると、事後に事実を復元できない。
//      **とくに null が意味を持つ**——CloseDecisionId / CloseIntent が無いことは
//      「手仕舞いレグが無い（取消 or 解消失敗）」という積極的な意味である（IADR-0210 決定3）。
//   2. **接続不可の分類が原因例外を保つこと。** 受け手は本例外で予約を解放するため、
//      分類だけが残って原因が消えると、切り分けの手掛かりが失われる（IADR-0211）。
public class ProtectiveStopEventPayloadTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 28, 6, 0, 0, TimeSpan.Zero);

    private static OrderIntent EntryIntent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
            10, 1_000m, PositionEffect.Open, StopLossPrice: 950m, FxRateToBase: 1m);

    private static OrderIntent CloseIntent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
            10, 950m, PositionEffect.Close, StopLossPrice: null, FxRateToBase: 1m);

    private static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    [Fact]
    public void 見送りはJSONを往復しても理由と注文意図を保つ()
    {
        var evt = new OrderDispatchForgone(
            Guid.NewGuid(), EntryIntent(), OrderDispatchForgoneReason.BrokerUnavailable, T0);

        RoundTrip(evt).Should().Be(evt);
    }

    [Fact]
    public void 保護逆指値の発注はJSONを往復しても決済意図とトリガーを保つ()
    {
        var evt = new ProtectiveStopPlaced(
            Guid.NewGuid(), Guid.NewGuid(), "stop-1", CloseIntent(), 950m, 1, T0);

        var restored = RoundTrip(evt);

        restored.Should().Be(evt);
        restored.CloseIntent.PositionEffect.Should().Be(PositionEffect.Close);
    }

    // 🔴 null が意味を持つ側を必ず往復させる。手仕舞いレグの有無は台帳結線の分岐そのものである。
    [Theory]
    [InlineData(ProtectiveStopRemediation.EntryCancelled)]
    [InlineData(ProtectiveStopRemediation.None)]
    public void 手仕舞いレグを伴わない保護喪失は往復してもnullのまま(ProtectiveStopRemediation remediation)
    {
        var evt = new ProtectiveStopCoverageLost(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, ProtectiveStopLossCause.RejectedAtEntry,
            remediation, 10, CloseDecisionId: null, CloseIntent: null, T0);

        var restored = RoundTrip(evt);

        restored.Should().Be(evt);
        restored.CloseDecisionId.Should().BeNull();
        restored.CloseIntent.Should().BeNull();
    }

    // #848, IADR-0117（2026-09-19 追記・改定 7）: CloseDispatchIndeterminate（成行手仕舞いの結果が未確認）も
    // 手仕舞いレグを運ぶ。往復で落ちると台帳が押さえられない。
    [Theory]
    [InlineData(ProtectiveStopRemediation.PositionClosed)]
    [InlineData(ProtectiveStopRemediation.CloseDispatchIndeterminate)]
    public void 手仕舞いレグを伴う保護喪失は往復しても決済意図を保つ(ProtectiveStopRemediation remediation)
    {
        var evt = new ProtectiveStopCoverageLost(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, ProtectiveStopLossCause.LapsedInFlight,
            remediation, 10, Guid.NewGuid(), CloseIntent(), T0);

        var restored = RoundTrip(evt);

        restored.Should().Be(evt);
        restored.Remediation.Should().Be(remediation);
        restored.CloseIntent!.Quantity.Should().Be(10);
    }

    // 🔴 列挙は末尾へ足す（既存値の序数を動かさない）。序数で永続化・送受信されても過去の記録の意味が変わらない。
    [Fact]
    public void 保護喪失への対処の列挙は既存値の序数を動かさない()
    {
        ((int)ProtectiveStopRemediation.EntryCancelled).Should().Be(0);
        ((int)ProtectiveStopRemediation.PositionClosed).Should().Be(1);
        ((int)ProtectiveStopRemediation.None).Should().Be(2);
        ((int)ProtectiveStopRemediation.CloseDispatchIndeterminate).Should().Be(3);
    }

    // FR-10, FR-11, ADR-0040 決定1（S3）, #821, IADR-0347: 🔴 **拒否理由が往復で欠落しないこと**。
    // この payload が監査台帳に残る「なぜ S3 が使えないのか」の唯一の一次証跡である。
    [Fact]
    public void 代替注文種別の試行は往復しても注文種別と拒否理由を保つ()
    {
        var evt = new AlternativeProtectiveStopAttempted(
            Guid.NewGuid(), Guid.NewGuid(), "AAPL", Market.UnitedStates,
            AlternativeProtectiveOrderType.StopLimit, OrderStatus.Rejected, "alt-1",
            1, "Paper trading does not support StopLimit order",
            StopLossExecutionMethod.AlternativeBrokerOrderType, BrokerProvider.MoomooSimulate, T0);

        var restored = RoundTrip(evt);

        restored.Should().Be(evt);
        restored.RejectReasonCode.Should().Be(1);
        restored.RejectReasonMessage.Should().Be("Paper trading does not support StopLimit order");
    }

    // 受理された試行は理由を持たない（null が「断られていない」という積極的な意味を持つ）。
    [Fact]
    public void 受理された代替注文種別の試行は理由なしのまま往復する()
    {
        var evt = new AlternativeProtectiveStopAttempted(
            Guid.NewGuid(), Guid.NewGuid(), "AAPL", Market.UnitedStates,
            AlternativeProtectiveOrderType.TrailingStop, OrderStatus.Accepted, "alt-2",
            null, null, StopLossExecutionMethod.AlternativeBrokerOrderType, BrokerProvider.MoomooSimulate, T0);

        var restored = RoundTrip(evt);

        restored.Should().Be(evt);
        restored.RejectReasonCode.Should().BeNull();
        restored.RejectReasonMessage.Should().BeNull();
    }

    [Fact]
    public void 接続不可の分類は原因例外を保つ()
    {
        var cause = new TimeoutException("接続応答なし（テスト）");

        var classified = new BrokerUnavailableException("OpenD への接続を確立できませんでした（未発注）。", cause);

        classified.InnerException.Should().BeSameAs(cause);
        classified.Message.Should().Contain("未発注");
    }

    [Fact]
    public void 接続不可の分類は原因なしでも成立する()
    {
        // InitConnect の戻り値が false のように、包むべき例外が無い失敗も分類の対象である。
        var classified = new BrokerUnavailableException("OpenD への InitConnect が失敗しました。");

        classified.InnerException.Should().BeNull();
        classified.Should().BeAssignableTo<Exception>();
    }
}
