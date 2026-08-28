using OrderExecutionService.Application.Adapters;
using OrderExecutionService.Application.Ports;
using OrderExecutionService.Infrastructure.Retention;
using AiStockTrading.Shared.Contracts.Operations;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace OrderExecutionService.Infrastructure.Tests;

// NFR（運用）, #137, FR-05, IADR-0059: 発注予約の保持期間パージジョブ。
// 既定無効・cutoff は RetentionPolicy が決める・Reserved には触れない・例外でサービスを止めない。
public class OrderReservationRetentionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 3, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // パージ要求を記録するフェイク（述語自体は Ef/InMemory ストアのテストが担保する）。
    private sealed class RecordingStore : IOrderReservationStore
    {
        public List<(DateTimeOffset Cutoff, int BatchSize)> Calls { get; } = [];
        public int PurgeResult { get; set; }
        public Exception? ThrowOnPurge { get; set; }

        public bool TryReserve(Guid decisionId, DateTimeOffset reservedAt) => true;

        public void MarkCompleted(Guid decisionId, string brokerOrderId, DateTimeOffset completedAt) { }

        public OrderDispatchReservation? Find(Guid decisionId) => null;

        public IReadOnlyList<OrderDispatchReservation> FindStalledReserved(DateTimeOffset reservedBefore, int batchSize) => [];

        public bool Release(Guid decisionId) => false;

        public int PurgeCompletedBefore(DateTimeOffset cutoff, int batchSize)
        {
            Calls.Add((cutoff, batchSize));
            if (ThrowOnPurge is not null)
                throw ThrowOnPurge;
            return PurgeResult;
        }
    }

    private static (OrderReservationRetentionService Service, RecordingStore Store) Build(RetentionOptions options)
    {
        var store = new RecordingStore();
        var provider = new ServiceCollection()
            .AddSingleton<IOrderReservationStore>(store)
            .BuildServiceProvider(true);

        var service = new OrderReservationRetentionService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeClock(),
            Options.Create(options),
            NullLogger<OrderReservationRetentionService>.Instance);

        return (service, store);
    }

    [Fact]
    public async Task 無効なら_1_行も消さない()
    {
        var (service, store) = Build(new RetentionOptions { Enabled = false });

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        store.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task 巡回は_RetentionPolicy_が決めた_cutoff_でパージする()
    {
        var (service, store) = Build(new RetentionOptions { Enabled = true, RetentionDays = 90, BatchSize = 500 });

        await service.PurgeOnceAsync(CancellationToken.None);

        store.Calls.Should().ContainSingle();
        store.Calls[0].Cutoff.Should().Be(RetentionPolicy.CutoffFor(Now, 90));
        store.Calls[0].BatchSize.Should().Be(500);
    }

    [Fact]
    public async Task 保持期間の設定ミスは下限_7_日にクランプされる()
    {
        var (service, store) = Build(new RetentionOptions { Enabled = true, RetentionDays = -1 });

        await service.PurgeOnceAsync(CancellationToken.None);

        store.Calls[0].Cutoff.Should().Be(Now.AddDays(-RetentionPolicy.MinimumRetentionDays));
    }

    [Fact]
    public async Task パージの例外は握りつぶしてサービスを止めない()
    {
        var (service, store) = Build(new RetentionOptions { Enabled = true });
        store.ThrowOnPurge = new InvalidOperationException("DB 障害");

        // フェイルセーフ: パージ失敗で発注執行サービスごと落ちてはならない。
        var act = async () =>
        {
            await service.StartAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);
        };

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task 有効な巡回でも_Reserved_は残る_実ストア結線()
    {
        // 本ジョブを実ストアに繋いだときの最終的な安全性（Reserved を消さない）を、ジョブ側から確認する。
        var store = new InMemoryOrderReservationStore();
        var stuck = Guid.NewGuid();
        var done = Guid.NewGuid();
        store.TryReserve(stuck, Now.AddYears(-5));
        store.TryReserve(done, Now.AddYears(-5));
        store.MarkCompleted(done, "o1", Now.AddYears(-5));

        var provider = new ServiceCollection()
            .AddSingleton<IOrderReservationStore>(store)
            .BuildServiceProvider(true);
        var service = new OrderReservationRetentionService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeClock(),
            Options.Create(new RetentionOptions { Enabled = true }),
            NullLogger<OrderReservationRetentionService>.Instance);

        (await service.PurgeOnceAsync(CancellationToken.None)).Should().Be(1);

        store.Find(stuck)!.State.Should().Be(OrderDispatchState.Reserved);
        store.Find(done).Should().BeNull();
    }

    // NFR-10, NFR-11, FR-11, #339, IADR-0228: 本サービスが消すストアは**パージしてよいと宣言されている**ものだけである。
    // 重複排除メタデータ（NFR-08）。未確定の予約は終端化するまで消さない（NFR-09）
    //
    // 🔴 業務台帳・監査証跡（費用台帳・発注履歴・監査ログ）は 7 年保持でパージ対象外であり、
    // 配線がそちらを指した瞬間に `PurgeOnceAsync` が 1 行も消さずに落ちる（<c>RetentionScope.EnsurePurgeable</c>）。
    [Fact]
    public void パージ対象は保持区分の宣言でパージ可とされている()
    {
        RetentionScope.IsPurgeable("order_dispatch_reservations").Should().BeTrue();
    }

    // 否定形: 監査台帳を対象にすることはできない（7 年保持）。
    // **宣言を読むだけでなく、パージ経路が呼ぶ関門そのものが止めることを確かめる。**
    [Fact]
    public void 否定形_監査台帳をパージ対象にしようとすると関門が止める()
    {
        RetentionScope.IsPurgeable("audit_events").Should().BeFalse();

        var act = () => RetentionScope.EnsurePurgeable("audit_events");

        act.Should().Throw<InvalidOperationException>();
    }

    // 本サービスの対象は関門を通る（＝実際に呼ばれる PurgeOnceAsync が落ちない）。
    [Fact]
    public void パージ対象は関門を通る()
    {
        var act = () => RetentionScope.EnsurePurgeable("order_dispatch_reservations");

        act.Should().NotThrow();
    }
}
