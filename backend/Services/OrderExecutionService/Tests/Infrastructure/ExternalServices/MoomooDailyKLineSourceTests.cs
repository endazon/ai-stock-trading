using AwesomeAssertions;
using OrderExecutionService.Features.OrderExecution.ProbeKLineQuota;
using OrderExecutionService.Features.OrderExecution.QueryDailyBars;
using OrderExecutionService.Infrastructure.ExternalServices;
using Xunit;

namespace OrderExecutionService.Tests;

// T-10-1838, FR-04, FR-15, ADR-0048 決定 2・3, #1118, IADR-0467 決定 1: 日足の照会の moomoo 包み（MoomooDailyKLineSource）。
// 🔴 前復権に固定・日足の要求は 1 回・続きの鍵は失敗（途中までの足で比を作らない）・空白足と欄の欠けを捨てる・例外で接続を作り直す・
// 取得の直後に枠を照会する（照会の失敗は取得の結果を覆さない）。
public class MoomooDailyKLineSourceTests
{
    private static readonly DateOnly From = new(2026, 8, 14);
    private static readonly DateOnly To = new(2026, 9, 28);

    private static ProbeKLine K(string? time, double? close = 101, long? volume = 1_000, bool blank = false) =>
        new(time, blank, 100, 102, 99, close, volume, 1e6);

    [Fact]
    public async Task 前復権で期間を渡し_足を写し_取得の直後に枠を照会する()
    {
        var query = new FakeQuery(new DailyKLineReading(0, "", [K("2026-09-25 00:00:00"), K("2026-09-28 00:00:00", volume: 2_000)], false));
        using var source = new MoomooDailyKLineSource(() => query);

        var fetch = await source.FetchForwardAdjustedAsync("AAPL", From, To, TestContext.Current.CancellationToken);

        query.KLineRequests.Should().ContainSingle().Which.Should().Be(new DailyKLineProbeRequest("AAPL", KLineRehab.Forward, From, To));
        query.QuotaQueries.Should().Equal(false); // 詳細なし（枠は減らない照会）
        fetch.Should().BeEquivalentTo(new DailyKLineFetch(true,
            [new DailyBarView(new DateOnly(2026, 9, 25), 100m, 102m, 99m, 101m, 1_000),
             new DailyBarView(new DateOnly(2026, 9, 28), 100m, 102m, 99m, 101m, 2_000)], null, 5, 295),
            o => o.WithStrictOrdering());
    }

    [Fact]
    public void 空白足と欄の欠けと0以下の価格と負の出来高の足は捨てる()
    {
        var bars = MoomooDailyKLineSource.MapBars(
        [
            K("2026-09-21 00:00:00", blank: true),
            K(null),
            K("2026-9-22"),
            K("2026-09-23 00:00:00", close: null),
            K("2026-09-24 00:00:00", volume: null),
            K("2026-09-25 00:00:00", close: 0),
            K("2026-09-26 00:00:00", volume: -1),
            K("2026-09-28 00:00:00", volume: 0),
        ]);

        bars.Should().ContainSingle().Which.Date.Should().Be(new DateOnly(2026, 9, 28), "出来高 0 の足は値として写す（判断側が比の窓から外す）");
    }

    [Theory]
    [InlineData(-1, false, "ret--1")]
    [InlineData(0, true, "has-more")]
    public async Task 非成功と続きの鍵つきの応答は失敗にする(int retType, bool hasMore, string reason)
    {
        var query = new FakeQuery(new DailyKLineReading(retType, "err", [K("2026-09-28 00:00:00")], hasMore));
        using var source = new MoomooDailyKLineSource(() => query);

        var fetch = await source.FetchForwardAdjustedAsync("AAPL", From, To, TestContext.Current.CancellationToken);

        fetch.Succeeded.Should().BeFalse();
        fetch.FailureReason.Should().Be(reason);
        fetch.Bars.Should().BeEmpty("途中までの足で比を作らない");
        query.KLineRequests.Should().HaveCount(1, "撃ち直さない・続きを追わない");
    }

    [Fact]
    public async Task 接続は使い回し_例外で捨てて次の要求で作り直す()
    {
        var created = new List<FakeQuery>();
        var answers = new Queue<Func<DailyKLineReading>>([
            () => new DailyKLineReading(0, "", [K("2026-09-28 00:00:00")], false),
            () => throw new InvalidOperationException("OpenD（相場）との接続が切れた"),
            () => new DailyKLineReading(0, "", [K("2026-09-28 00:00:00")], false),
        ]);
        using var source = new MoomooDailyKLineSource(() =>
        {
            var q = new FakeQuery(() => answers.Dequeue()());
            created.Add(q);
            return q;
        });
        var ct = TestContext.Current.CancellationToken;

        await source.FetchForwardAdjustedAsync("AAPL", From, To, ct);
        var act = () => source.FetchForwardAdjustedAsync("MSFT", From, To, ct);
        await act.Should().ThrowAsync<InvalidOperationException>();
        await source.FetchForwardAdjustedAsync("NVDA", From, To, ct);

        created.Should().HaveCount(2, "1 本目を 2 回使い、例外の後に 2 本目を作る");
        created[0].Disposed.Should().BeTrue("例外の出た接続は捨てる");
        created[1].KLineRequests.Should().ContainSingle().Which.Symbol.Should().Be("NVDA");
    }

    [Fact]
    public async Task 枠の照会の失敗は取得の結果を覆さず枠を不明にする()
    {
        var query = new FakeQuery(new DailyKLineReading(0, "", [K("2026-09-28 00:00:00")], false)) { QuotaThrows = true };
        using var source = new MoomooDailyKLineSource(() => query);

        var fetch = await source.FetchForwardAdjustedAsync("AAPL", From, To, TestContext.Current.CancellationToken);

        fetch.Succeeded.Should().BeTrue();
        fetch.Bars.Should().ContainSingle();
        fetch.QuotaUsed.Should().BeNull();
        fetch.QuotaRemaining.Should().BeNull();
    }

    private sealed class FakeQuery(Func<DailyKLineReading> answer) : IKLineQuotaQuery, IDisposable
    {
        public FakeQuery(DailyKLineReading reading) : this(() => reading) { }

        public List<DailyKLineProbeRequest> KLineRequests { get; } = [];
        public List<bool> QuotaQueries { get; } = [];
        public bool QuotaThrows { get; init; }
        public bool Disposed { get; private set; }

        public Task<KLineQuotaReading> QueryQuotaAsync(bool includeDetail, CancellationToken cancellationToken = default)
        {
            QuotaQueries.Add(includeDetail);
            if (QuotaThrows)
                throw new TimeoutException("quota reply timeout");
            return Task.FromResult(new KLineQuotaReading(0, "", 5, 295, []));
        }

        public Task<DailyKLineReading> RequestDailyKLinesAsync(DailyKLineProbeRequest request, CancellationToken cancellationToken = default)
        {
            KLineRequests.Add(request);
            return Task.FromResult(answer());
        }

        public void Dispose() => Disposed = true;
    }
}
