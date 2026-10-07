using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using TradeDecisionService.Features.TradeDecision;
using Xunit;

namespace TradeDecisionService.Tests;

// 🔴 FR-04, FR-02, NFR-13, #1194, IADR-0505: 定時サイクルの再試行の連鎖全体（起点の待ち ＋ 試行 × T ＋ 再試行の待ち）が
// ブローカの consumer_timeout に収まる試行の上限の導出（純関数）。
public class ScheduledCycleRetryChainTests
{
    private static readonly TimeSpan[] SharedCooldowns =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(600);

    // T-10-2403: 試行の上限は「握り(n) ＝ 待ち ＋ n × T ＋ 最初の n − 1 回の待ち」が consumer_timeout より短い最大の n（共通の 4 回以下）。
    // 再試行の待ちは共通の間隔の先頭から取る。
    [Theory]
    [InlineData(100, 1800, 4, 1042)]  // 600 ＋ 4 × 100 ＋ 42（共通の上限いっぱい）
    [InlineData(340, 1800, 3, 1632)]  // 600 ＋ 3 × 340 ＋ 12（4 回目を足すと 2,002 で超える）
    [InlineData(579, 1800, 2, 1760)]  // 600 ＋ 2 × 579 ＋ 2
    [InlineData(579, 1760, 1, 1179)]  // 🔴 境界: 握り(2) がちょうど consumer_timeout なら 2 回にしない
    [InlineData(960, 1800, 1, 1560)]  // 既定（前提 10・1 銘柄 90 秒）＝ 定時サイクルは再試行しない
    [InlineData(1140, 1800, 1, 1740)] // 経路B（前提 12）＝ 再試行しない
    [InlineData(1199, 1800, 1, 1799)] // 1 回で収まる最大
    [InlineData(1140, 3600, 2, 2882)] // consumer_timeout を延ばした構成では再試行が戻る
    public void T_10_2403_試行の上限は再試行の連鎖全体がconsumer_timeoutに収まる最大の回数(
        int handlerSeconds, int consumerTimeoutSeconds, int expectedAttempts, int expectedHoldSeconds)
    {
        var chain = ScheduledCycleRetryChain.Derive(
            TimeSpan.FromSeconds(handlerSeconds), SharedCooldowns, Wait, TimeSpan.FromSeconds(consumerTimeoutSeconds));

        chain.Attempts.Should().Be(expectedAttempts);
        chain.Hold.Should().Be(TimeSpan.FromSeconds(expectedHoldSeconds));
        chain.Hold.Should().BeLessThan(TimeSpan.FromSeconds(consumerTimeoutSeconds));
        chain.Cooldowns.Should().Equal(SharedCooldowns.Take(expectedAttempts - 1));
        chain.BrokerConsumerTimeout.Should().Be(TimeSpan.FromSeconds(consumerTimeoutSeconds));
    }

    // T-10-2403: 起点の待ちも握りに数える（待ちを落とすと経路B で 4 回の再試行が収まると誤る）。
    [Fact]
    public void T_10_2403_起点の待ちを握りに数える()
    {
        var withoutWait = ScheduledCycleRetryChain.Derive(
            TimeSpan.FromSeconds(400), SharedCooldowns, TimeSpan.Zero, TimeSpan.FromSeconds(1800));
        var withWait = ScheduledCycleRetryChain.Derive(
            TimeSpan.FromSeconds(400), SharedCooldowns, Wait, TimeSpan.FromSeconds(1800));

        withoutWait.Attempts.Should().Be(4, "0 ＋ 4 × 400 ＋ 42 ＝ 1,642 ＜ 1,800");
        withWait.Attempts.Should().Be(2, "600 ＋ 3 × 400 ＋ 12 ＝ 1,812 は超える・600 ＋ 2 × 400 ＋ 2 ＝ 1,402 は収まる");
    }

    // T-10-2403: 本番の入力（共通の間隔・起点の待ち・既定の consumer_timeout）の値を固定する。
    [Fact]
    public void T_10_2403_本番の入力は共通の間隔と鮮度の既定と30分()
    {
        WolverineExtensions.RetryCooldowns.Should().Equal(SharedCooldowns, "共通の再試行 2s/10s/30s（IADR-0129 決定 5）");
        WolverineExtensions.RetryCooldowns.Count.Should().Be(WolverineExtensions.MaxDeliveryAttempts - 1);
        ScheduledCycleRetryChain.QueueWaitAllowance.Should().Be(TimeSpan.FromSeconds(600), "巡回 300 秒の鮮度の上限・宣言なしの既定");
        ScheduledCycleRetryChain.DefaultBrokerConsumerTimeoutSeconds.Should().Be(1800, "RabbitMQ の既定 30 分");
    }

    // 🔴 T-10-2404（否定形）: 試行 1 回でも consumer_timeout に収まらない構成は導かない（起動を止める）。ちょうど等しいときも止める。
    [Theory]
    [InlineData(1230, 1800)] // 経路B で前提を 13 へ上げた（13 × 90 ＋ 60）: 600 ＋ 1,230 ＝ 1,830
    [InlineData(1200, 1800)] // ちょうど 1,800
    [InlineData(2000, 1800)] // T だけで超える
    public void T_10_2404_試行1回でも収まらない構成は起動を止める(int handlerSeconds, int consumerTimeoutSeconds)
    {
        var act = () => ScheduledCycleRetryChain.Derive(
            TimeSpan.FromSeconds(handlerSeconds), SharedCooldowns, Wait, TimeSpan.FromSeconds(consumerTimeoutSeconds));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{ScheduledCycleRetryChain.BrokerConsumerTimeoutKey}*")
            .WithMessage($"*{ScheduledCycleBudget.MaxWatchedSymbolsKey}*");
    }

    // T-10-2404（否定形）: 0 秒の上限・負の待ち・0 秒の consumer_timeout は受け付けない。
    [Fact]
    public void T_10_2404_非正の入力は導かない()
    {
        var zeroHandler = () => ScheduledCycleRetryChain.Derive(TimeSpan.Zero, SharedCooldowns, Wait, TimeSpan.FromSeconds(1800));
        var negativeWait = () => ScheduledCycleRetryChain.Derive(
            TimeSpan.FromSeconds(1), SharedCooldowns, TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1800));
        var zeroBroker = () => ScheduledCycleRetryChain.Derive(TimeSpan.FromSeconds(1), SharedCooldowns, Wait, TimeSpan.Zero);

        zeroHandler.Should().Throw<ArgumentOutOfRangeException>();
        negativeWait.Should().Throw<ArgumentOutOfRangeException>();
        zeroBroker.Should().Throw<ArgumentOutOfRangeException>();
    }

    // T-10-2403: consumer_timeout の構成値は、未設定・空・不正・0・負なら既定 1,800 秒へ倒す（0 や負で起動を止めない）。
    [Theory]
    [InlineData(null, 1800)]
    [InlineData("", 1800)]
    [InlineData("abc", 1800)]
    [InlineData("0", 1800)]
    [InlineData("-5", 1800)]
    [InlineData("3600", 3600)]
    public void T_10_2403_consumer_timeoutの構成値を読む(string? value, int expectedSeconds) =>
        ScheduledCycleRetryChain.ParseBrokerConsumerTimeout(value).Should().Be(TimeSpan.FromSeconds(expectedSeconds));
}
