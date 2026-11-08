using System.Collections.Concurrent;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.TestSupport.Metrics;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// 🔴 NFR, FR-10, IADR-0645 決定 2: 発行元の確かめの再送の記録を、受け手の DB（置き場）にも残す。
//   AC3-1: 置き場があれば、検証器を作り直した（再起動の）後も処理し終えた ID の再送を拒む。陰性対照: 置き場が無ければ通る。
//   AC3-2: 置き場の読みの失敗は例外（拒否として捨てない）。書きの失敗は計器に残し、プロセスの中の記録で続ける。
//   AC3-3: 置き場の無い受け手は、起動より前に猶予を超えて発行されたものを unrecorded とする（段 2 は処理しない・段 1 は記録して通す）。
// 実 PostgreSQL の置き場は Integration（MessageOriginReplayStoreE2ETests）が確かめる。ここは置き場の契約を写した試験用の置き場で確かめる。
public class MessageOriginReplayStoreTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private readonly MutableTimeProvider _clock = new();

    private MessageOriginVerifier Verifier(
        MessageOriginMode mode, IMessageOriginReplayStore? store = null, DateTimeOffset? unrecordedBefore = null, BusinessMetrics? metrics = null) =>
        new(mode, MessageOriginTestKeys.Receiver(), TimeSpan.FromHours(24), TimeSpan.FromMinutes(5), _clock,
            NullLogger<MessageOriginVerifier>.Instance, metrics, replayStore: store, unrecordedBefore: unrecordedBefore);

    // 発行元（発注執行）の口で今の時刻に署名した約定。受け手が受け取ったときの形（別の封筒の実体）を返す関数も返す。
    private Func<Envelope> SignedNow()
    {
        var sent = new Envelope(MessageOriginSamples.Create(typeof(OrderExecuted)));
        new MessageOriginSigningRule(
            MessageOriginCatalog.OrderExecution, MessageOriginTestKeys.Signer(MessageOriginCatalog.OrderExecution), _clock).Modify(sent);
        return () =>
        {
            var received = new Envelope(sent.Message!);
            foreach (var (k, v) in sent.Headers)
                received.Headers[k] = v;
            return received;
        };
    }

    [Fact]
    public async Task 置き場があれば検証器を作り直した後も処理し終えた再送を段2で拒む()
    {
        var store = new FakeReplayStore();
        var delivery = SignedNow();
        var before = Verifier(MessageOriginMode.Enforce, store);
        var first = delivery();
        (await before.AdmitAsync(first, CancellationToken.None)).Should().BeTrue();
        await before.MarkHandledAsync(first, CancellationToken.None);

        _clock.Advance(TimeSpan.FromHours(1));
        var after = Verifier(MessageOriginMode.Enforce, store); // 再起動（プロセスの中の記録は消えた）
        var replay = delivery();
        (await after.CheckAsync(replay, CancellationToken.None)).Should().Be(MessageOriginCheck.Replayed);
        (await after.AdmitAsync(replay, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task 陰性対照_置き場が無く猶予も与えなければ再起動の後の再送は通ってしまう()
    {
        var delivery = SignedNow();
        var before = Verifier(MessageOriginMode.Enforce);
        var first = delivery();
        (await before.AdmitAsync(first, CancellationToken.None)).Should().BeTrue();
        await before.MarkHandledAsync(first, CancellationToken.None);

        _clock.Advance(TimeSpan.FromHours(1));
        (await Verifier(MessageOriginMode.Enforce).CheckAsync(delivery(), CancellationToken.None)).Should().Be(MessageOriginCheck.Valid);
    }

    [Fact]
    public async Task 置き場の記録は窓を過ぎれば消え時刻の検査が拒む()
    {
        var store = new FakeReplayStore();
        var delivery = SignedNow();
        var first = delivery();
        var verifier = Verifier(MessageOriginMode.Enforce, store);
        await verifier.MarkHandledAsync(first, CancellationToken.None);
        store.Count.Should().Be(1);

        _clock.Advance(TimeSpan.FromHours(25));
        (await Verifier(MessageOriginMode.Enforce, store).CheckAsync(delivery(), CancellationToken.None)).Should().Be(MessageOriginCheck.Stale);
        await Verifier(MessageOriginMode.Enforce, store).MarkHandledAsync(SignedNow()(), CancellationToken.None);
        store.Count.Should().Be(1, "期限を過ぎた記録は捨て、新しい 1 件だけが残る");
    }

    [Fact]
    public async Task 置き場を読めなければ例外で処理を止め拒否には数えない()
    {
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        var store = new FakeReplayStore() { FailLookup = true };
        var verifier = Verifier(MessageOriginMode.Enforce, store, metrics: metrics);

        var act = async () => await verifier.AdmitAsync(SignedNow()(), CancellationToken.None);

        await act.Should().ThrowAsync<MessageOriginReplayStoreUnavailableException>();
        capture.TagValuesOf(BusinessMetricNames.MessageOriginReplayStoreFailures, BusinessMetricNames.TagOperation)
            .Should().Equal("lookup");
        capture.ValuesOf(BusinessMetricNames.MessageOriginFailures).Should().BeEmpty("捨てていない（再試行する）");
    }

    [Fact]
    public async Task 置き場に書けなくても処理は止めずプロセスの中の記録で再送を拒む()
    {
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        var store = new FakeReplayStore() { FailRecord = true };
        var verifier = Verifier(MessageOriginMode.Enforce, store, metrics: metrics);
        var delivery = SignedNow();
        var first = delivery();

        await verifier.MarkHandledAsync(first, CancellationToken.None);

        capture.TagValuesOf(BusinessMetricNames.MessageOriginReplayStoreFailures, BusinessMetricNames.TagOperation)
            .Should().Equal("record");
        (await verifier.CheckAsync(delivery(), CancellationToken.None)).Should().Be(MessageOriginCheck.Replayed);
    }

    [Fact]
    public async Task 置き場が無い受け手は起動より前に猶予を超えて発行されたものを段2で処理しない()
    {
        var old = SignedNow();
        _clock.Advance(Grace + TimeSpan.FromMinutes(1));
        var withinGrace = SignedNow();
        _clock.Advance(Grace - TimeSpan.FromMinutes(1));
        var startedAt = _clock.GetUtcNow();
        var verifier = Verifier(MessageOriginMode.Enforce, unrecordedBefore: startedAt - Grace);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var afterStart = SignedNow();

        (await verifier.CheckAsync(old(), CancellationToken.None)).Should().Be(MessageOriginCheck.Unrecorded);
        (await verifier.AdmitAsync(old(), CancellationToken.None)).Should().BeFalse();
        (await verifier.CheckAsync(withinGrace(), CancellationToken.None)).Should().Be(MessageOriginCheck.Valid, "入れ替えの間に送られたもの");
        (await verifier.CheckAsync(afterStart(), CancellationToken.None)).Should().Be(MessageOriginCheck.Valid);

        // 陰性対照: 置き場がある受け手は起動の時刻で区切らない（記録で確かめる）。
        (await Verifier(MessageOriginMode.Enforce, new FakeReplayStore()).CheckAsync(old(), CancellationToken.None))
            .Should().Be(MessageOriginCheck.Valid);
    }

    [Fact]
    public async Task 置き場が無い受け手でも段1は起動より前のものを記録して通す()
    {
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        var old = SignedNow();
        _clock.Advance(TimeSpan.FromHours(1));
        var verifier = Verifier(MessageOriginMode.Report, unrecordedBefore: _clock.GetUtcNow() - Grace, metrics: metrics);

        (await verifier.AdmitAsync(old(), CancellationToken.None)).Should().BeTrue();

        capture.TagValuesOf(BusinessMetricNames.MessageOriginFailures, BusinessMetricNames.TagReason).Should().Equal("unrecorded");
        capture.TagValuesOf(BusinessMetricNames.MessageOriginFailures, BusinessMetricNames.TagOutcome).Should().Equal("admitted");
    }

    // 置き場の契約（期限つきの記録・遅い方の期限に揃える・期限切れを捨てる）を写した試験用の置き場。
    private sealed class FakeReplayStore : IMessageOriginReplayStore
    {
        private readonly ConcurrentDictionary<Guid, DateTimeOffset> _rows = new();

        public bool FailLookup { get; init; }

        public bool FailRecord { get; init; }

        public int Count => _rows.Count;

        public ValueTask<bool> ContainsAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (FailLookup)
                throw new InvalidOperationException("置き場に繋がらない（試験）");
            return ValueTask.FromResult(_rows.TryGetValue(id, out var expires) && expires > now);
        }

        public ValueTask RecordAsync(Guid id, DateTimeOffset expires, CancellationToken cancellationToken)
        {
            if (FailRecord)
                throw new InvalidOperationException("置き場に書けない（試験）");
            _rows.AddOrUpdate(id, expires, (_, old) => old > expires ? old : expires);
            return ValueTask.CompletedTask;
        }

        public ValueTask PruneAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            foreach (var (id, expires) in _rows)
            {
                if (expires < now)
                    _rows.TryRemove(id, out _);
            }

            return ValueTask.CompletedTask;
        }
    }
}
