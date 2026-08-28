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

    [Fact]
    public void 手仕舞いレグを伴う保護喪失は往復しても決済意図を保つ()
    {
        var evt = new ProtectiveStopCoverageLost(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, ProtectiveStopLossCause.LapsedInFlight,
            ProtectiveStopRemediation.PositionClosed, 10, Guid.NewGuid(), CloseIntent(), T0);

        var restored = RoundTrip(evt);

        restored.Should().Be(evt);
        restored.CloseIntent!.Quantity.Should().Be(10);
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
