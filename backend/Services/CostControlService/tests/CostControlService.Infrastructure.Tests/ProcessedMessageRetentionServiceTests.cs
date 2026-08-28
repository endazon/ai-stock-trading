using AiStockTrading.CostControl.Application.Ports;
using AiStockTrading.CostControl.Infrastructure.Composable.Retention;
using AiStockTrading.Shared.Contracts.Operations;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiStockTrading.CostControl.Infrastructure.Tests;

// NFR（運用）, #137, IADR-0059: processed_messages のパージジョブ。
// 既定無効（不可逆な DELETE はオプトイン）・cutoff は RetentionPolicy が決める・例外でサービスを止めない。
public class ProcessedMessageRetentionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 3, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // パージ要求を記録するだけのフェイク（実際の述語は Ef/InMemory ストアのテストが担保する）。
    private sealed class RecordingStore : IProcessedMessageStore
    {
        public List<(DateTimeOffset Cutoff, int BatchSize)> Calls { get; } = [];
        public int PurgeResult { get; set; }
        public Exception? ThrowOnPurge { get; set; }

        public bool TryMarkProcessed(Guid messageId, DateTimeOffset at) => true;

        public void Unmark(Guid messageId) { }

        public int PurgeProcessedBefore(DateTimeOffset cutoff, int batchSize)
        {
            Calls.Add((cutoff, batchSize));
            if (ThrowOnPurge is not null)
                throw ThrowOnPurge;
            return PurgeResult;
        }
    }

    private static (ProcessedMessageRetentionService Service, RecordingStore Store) Build(RetentionOptions options)
    {
        var store = new RecordingStore();
        var provider = new ServiceCollection()
            .AddSingleton<IProcessedMessageStore>(store)
            .BuildServiceProvider(true);

        var service = new ProcessedMessageRetentionService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeClock(),
            Options.Create(options),
            NullLogger<ProcessedMessageRetentionService>.Instance);

        return (service, store);
    }

    [Fact]
    public void 既定は無効である()
    {
        // IADR-0059 決定4: 不可逆な DELETE の自動実行はオプトイン。
        new RetentionOptions().Enabled.Should().BeFalse();
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
        // 設定ミス（0 日）でも直近の行は消えない＝進行中メッセージの誤削除防止（IADR-0059 決定3）。
        var (service, store) = Build(new RetentionOptions { Enabled = true, RetentionDays = 0 });

        await service.PurgeOnceAsync(CancellationToken.None);

        store.Calls[0].Cutoff.Should().Be(Now.AddDays(-RetentionPolicy.MinimumRetentionDays));
    }

    [Fact]
    public async Task パージの例外は握りつぶしてサービスを止めない()
    {
        var (service, store) = Build(new RetentionOptions { Enabled = true });
        store.ThrowOnPurge = new InvalidOperationException("DB 障害");

        // フェイルセーフ: パージ失敗で費用統制サービスごと落ちてはならない。
        var act = async () =>
        {
            await service.StartAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);
        };

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task 削除件数を返す()
    {
        var (service, store) = Build(new RetentionOptions { Enabled = true });
        store.PurgeResult = 42;

        (await service.PurgeOnceAsync(CancellationToken.None)).Should().Be(42);
    }

    // NFR-10, NFR-11, FR-11, #339, IADR-0228: 本サービスが消すストアは**パージしてよいと宣言されている**ものだけである。
    // 重複排除メタデータ（NFR-08）
    //
    // 🔴 業務台帳・監査証跡（費用台帳・発注履歴・監査ログ）は 7 年保持でパージ対象外であり、
    // 配線がそちらを指した瞬間に `PurgeOnceAsync` が 1 行も消さずに落ちる（<c>RetentionScope.EnsurePurgeable</c>）。
    [Fact]
    public void パージ対象は保持区分の宣言でパージ可とされている()
    {
        RetentionScope.IsPurgeable("processed_messages").Should().BeTrue();
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
        var act = () => RetentionScope.EnsurePurgeable("processed_messages");

        act.Should().NotThrow();
    }
}
