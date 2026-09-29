using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// T-10-1737・T-10-1738（分類の表）, FR-10, #1093, IADR-0458: 保護逆指値ガードの建玉照会は、分類できた一時的な失敗に限って巡回の中で 1 回だけ照会し直す。
// 使い切ったら null（据え置き）。分類できない失敗・業務上の失敗は照会し直さない（失敗も頻度制限の枠を消費する。IADR-0144 決定 5）。
public class PositionQueryRetryTests
{
    private static readonly IReadOnlyList<BrokerPositionSnapshot> Held =
        [new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 10, 100m)];

    // 与えた結果を順に返す照会の口。呼ばれた回数を数える。
    private sealed class ScriptedSource(params PositionQueryResult[] results) : IClassifiedPositionSource
    {
        private readonly Queue<PositionQueryResult> _results = new(results);
        public int Calls { get; private set; }

        public Task<PositionQueryResult> QueryPositionsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(_results.Count > 1 ? _results.Dequeue() : _results.Peek());
        }
    }

    // 待ちを記録するだけ（壁時計を使わない）。
    private sealed class RecordedDelays
    {
        public List<TimeSpan> Waits { get; } = [];

        public Task Delay(TimeSpan wait, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Waits.Add(wait);
            return Task.CompletedTask;
        }
    }

    private static (PositionQueryRetry Retry, RecordedDelays Delays) NewRetry(
        ProtectiveStopGuardOptions? options = null, double random = 0.5)
    {
        var delays = new RecordedDelays();
        return (new PositionQueryRetry(options ?? new ProtectiveStopGuardOptions(), delays.Delay, () => random), delays);
    }

    private static PositionQueryResult Fail(PositionQueryFailure failure) => PositionQueryResult.Failed(failure);

    [Fact]
    public async Task 成功なら照会は_1_回で待たない()
    {
        var source = new ScriptedSource(PositionQueryResult.Success(Held));
        var (retry, delays) = NewRetry();

        var positions = await retry.QueryAsync(source, CancellationToken.None);

        positions.Should().BeSameAs(Held);
        source.Calls.Should().Be(1);
        delays.Waits.Should().BeEmpty();
    }

    [Fact]
    public async Task 一時的な失敗の後は_2_秒待って_1_回だけ照会し直し_成功を返す()
    {
        var source = new ScriptedSource(Fail(PositionQueryFailure.Transient), PositionQueryResult.Success(Held));
        var (retry, delays) = NewRetry();

        var positions = await retry.QueryAsync(source, CancellationToken.None);

        positions.Should().BeSameAs(Held);
        source.Calls.Should().Be(2);
        delays.Waits.Should().Equal(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task 頻度制限の後は_10_秒待って照会し直す()
    {
        var source = new ScriptedSource(Fail(PositionQueryFailure.RateLimited), PositionQueryResult.Success(Held));
        var (retry, delays) = NewRetry();

        await retry.QueryAsync(source, CancellationToken.None);

        delays.Waits.Should().Equal(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task 一時的な失敗が続けば照会は_2_回までで_null_を返す()
    {
        var source = new ScriptedSource(Fail(PositionQueryFailure.Transient));
        var (retry, _) = NewRetry();

        var positions = await retry.QueryAsync(source, CancellationToken.None);

        positions.Should().BeNull("使い切ったら据え置き（空列や推定値で代用しない）");
        source.Calls.Should().Be(2);
    }

    [Theory]
    [InlineData(PositionQueryFailure.Other)]
    [InlineData(PositionQueryFailure.None)]
    public async Task 分類できない失敗は照会し直さない(PositionQueryFailure failure)
    {
        var source = new ScriptedSource(Fail(failure));
        var (retry, delays) = NewRetry();

        var positions = await retry.QueryAsync(source, CancellationToken.None);

        positions.Should().BeNull();
        source.Calls.Should().Be(1, "失敗も頻度制限の枠を消費するため、一時的と言い切れないものは照会し直さない");
        delays.Waits.Should().BeEmpty();
    }

    [Fact]
    public async Task 待ちが予算_巡回間隔の半分_を超えるなら照会し直さない()
    {
        // 巡回 10 秒 → 予算 5 秒。頻度制限の待ち 10 秒は超える。
        var source = new ScriptedSource(Fail(PositionQueryFailure.RateLimited), PositionQueryResult.Success(Held));
        var (retry, delays) = NewRetry(new ProtectiveStopGuardOptions { Interval = TimeSpan.FromSeconds(10) });

        var positions = await retry.QueryAsync(source, CancellationToken.None);

        positions.Should().BeNull("待ちが予算を超える照会し直しはしない");
        source.Calls.Should().Be(1);
        delays.Waits.Should().BeEmpty();
    }

    [Fact]
    public async Task 待ちがちょうど予算なら照会し直す()
    {
        // 巡回 20 秒 → 予算 10 秒。頻度制限の待ち 10 秒（揺らぎ 0）はちょうど予算。
        var source = new ScriptedSource(Fail(PositionQueryFailure.RateLimited), PositionQueryResult.Success(Held));
        var (retry, delays) = NewRetry(new ProtectiveStopGuardOptions { Interval = TimeSpan.FromSeconds(20) });

        var positions = await retry.QueryAsync(source, CancellationToken.None);

        positions.Should().BeSameAs(Held);
        delays.Waits.Should().Equal(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(1.0, 3.0)]
    public async Task 待ちは基準に揺らぎ_既定50パーセント_を掛ける(double random, double expectedSeconds)
    {
        var source = new ScriptedSource(Fail(PositionQueryFailure.Transient), PositionQueryResult.Success(Held));
        var (retry, delays) = NewRetry(random: random);

        await retry.QueryAsync(source, CancellationToken.None);

        delays.Waits.Should().Equal(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 2)]
    [InlineData(-3, 1)]
    public async Task 照会し直しの回数は_0_から_1_に収める(int configured, int expectedCalls)
    {
        var source = new ScriptedSource(Fail(PositionQueryFailure.Transient));
        var (retry, _) = NewRetry(new ProtectiveStopGuardOptions { PositionQueryMaxRetries = configured });

        await retry.QueryAsync(source, CancellationToken.None);

        source.Calls.Should().Be(expectedCalls);
    }

    [Fact]
    public async Task 取り消されたら照会し直さずに中断する()
    {
        using var cts = new CancellationTokenSource();
        var source = new ScriptedSource(Fail(PositionQueryFailure.Transient), PositionQueryResult.Success(Held));
        var retry = new PositionQueryRetry(
            new ProtectiveStopGuardOptions(),
            (_, _) =>
            {
                cts.Cancel();
                return Task.FromCanceled(cts.Token);
            },
            () => 0.5);

        var act = async () => await retry.QueryAsync(source, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        source.Calls.Should().Be(1);
    }

    // --- 分類（moomoo の例外 → 照会し直してよいか） ---------------------------------------------

    public static TheoryData<string, Exception, PositionQueryFailure> Classifications() => new()
    {
        { "返信待ちの打ち切り", new TimeoutException(), PositionQueryFailure.Transient },
        { "接続の確立の失敗", new BrokerUnavailableException("x"), PositionQueryFailure.Transient },
        { "-100 打ち切り", new MoomooTradeRequestException("op", MoomooRetType.TimeOut, ""), PositionQueryFailure.Transient },
        { "-200 切断", new MoomooTradeRequestException("op", MoomooRetType.DisConnect, ""), PositionQueryFailure.Transient },
        { "-400 不明", new MoomooTradeRequestException("op", MoomooRetType.Unknown, ""), PositionQueryFailure.Transient },
        { "-500 応答の読み損ね", new MoomooTradeRequestException("op", MoomooRetType.Invalid, ""), PositionQueryFailure.Other },
        { "未定義の値", new MoomooTradeRequestException("op", -999, "high frequency"), PositionQueryFailure.Other },
        { "頻度制限（英・回数）", new MoomooTradeRequestException("op", MoomooRetType.Failed, "Maximum 10 times per 30 seconds"), PositionQueryFailure.RateLimited },
        { "頻度制限（英・語）", new MoomooTradeRequestException("op", MoomooRetType.Failed, "Request too High Frequency"), PositionQueryFailure.RateLimited },
        { "頻度制限（中）", new MoomooTradeRequestException("op", MoomooRetType.Failed, "请求频率太高"), PositionQueryFailure.RateLimited },
        { "頻度制限（日）", new MoomooTradeRequestException("op", MoomooRetType.Failed, "照会の頻度が上限を超えました"), PositionQueryFailure.RateLimited },
        { "業務上の失敗", new MoomooTradeRequestException("op", MoomooRetType.Failed, "account not found"), PositionQueryFailure.Other },
        { "分類できない例外", new InvalidOperationException("x"), PositionQueryFailure.Other },
    };

    [Theory]
    [MemberData(nameof(Classifications))]
    public void 建玉照会の失敗の分類(string label, Exception exception, PositionQueryFailure expected) =>
        MoomooPositionQueryClassifier.Classify(exception).Should().Be(expected, label);
}
