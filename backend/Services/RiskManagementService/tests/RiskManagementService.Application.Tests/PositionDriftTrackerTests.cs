using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-05, FR-09, FR-10, #292, #305, IADR-0118, IADR-0124: 乖離の報告可否。
// 一過性の未反映（発注直後〜約定反映待ち）で鳴らないための連続観測条件と、通知過多を避けるシグネチャ dedup。
// #305 以降は追跡状態を共有ストアに置くため、**複数レプリカに分散した観測でも連続条件が成立する**。
public class PositionDriftTrackerTests
{
    private static PositionDriftItem Drift(string symbol, int ledger, int broker, PositionDriftKind kind) =>
        new(symbol, Market.UnitedStates, ledger, broker, kind);

    private static readonly PositionDriftItem[] DriftA =
        [Drift("AAPL", 100, 80, PositionDriftKind.QuantityMismatch)];

    private static readonly PositionDriftItem[] DriftB =
        [Drift("AAPL", 100, 60, PositionDriftKind.QuantityMismatch)];

    private static PositionDriftTracker NewTracker(
        IPositionDriftStateStore store,
        int required = PositionDriftTracker.DefaultRequiredConsecutiveObservations) =>
        new(store, NullLogger<PositionDriftTracker>.Instance, required);

    private static PositionDriftTracker NewTracker() => NewTracker(new InMemoryPositionDriftStateStore());

    [Fact]
    public void 一度きりの乖離は報告しない()
    {
        // 発注直後〜約定ポーリング反映までの一過性のズレを弾く。
        NewTracker().ShouldReport(DriftA).Should().BeFalse();
    }

    [Fact]
    public void 連続で同一なら報告する()
    {
        var tracker = NewTracker();

        tracker.ShouldReport(DriftA).Should().BeFalse();
        tracker.ShouldReport(DriftA).Should().BeTrue();
    }

    [Fact]
    public void 継続している同一の乖離は再報告しない()
    {
        // 10 分ごとに Discord を叩かない。
        var tracker = NewTracker();
        tracker.ShouldReport(DriftA);
        tracker.ShouldReport(DriftA).Should().BeTrue();

        tracker.ShouldReport(DriftA).Should().BeFalse();
        tracker.ShouldReport(DriftA).Should().BeFalse();
    }

    [Fact]
    public void 乖離の内容が変われば連続条件を数え直す()
    {
        var tracker = NewTracker();
        tracker.ShouldReport(DriftA);
        tracker.ShouldReport(DriftA).Should().BeTrue();

        // 別内容は 1 回目では報告しない（内容が動いている間は一過性の可能性がある）。
        tracker.ShouldReport(DriftB).Should().BeFalse();
        tracker.ShouldReport(DriftB).Should().BeTrue();
    }

    [Fact]
    public void 乖離が解消したら報告しない()
    {
        var tracker = NewTracker();
        tracker.ShouldReport(DriftA);
        tracker.ShouldReport(DriftA).Should().BeTrue();

        tracker.ShouldReport([]).Should().BeFalse();
        tracker.ShouldReport([]).Should().BeFalse();
    }

    [Fact]
    public void 解消後に同じ乖離が再発したら再び報告する()
    {
        var tracker = NewTracker();
        tracker.ShouldReport(DriftA);
        tracker.ShouldReport(DriftA).Should().BeTrue();
        tracker.ShouldReport([]);
        tracker.ShouldReport([]);

        tracker.ShouldReport(DriftA).Should().BeFalse();
        tracker.ShouldReport(DriftA).Should().BeTrue();
    }

    [Fact]
    public void 順序が違うだけの同一集合は同じ乖離として扱う()
    {
        // 列挙順はブローカ応答に依存する。順序差で「内容が変わった」と誤認すると連続条件が永久に満たされない。
        var tracker = NewTracker();
        PositionDriftItem[] forward =
        [
            Drift("AAPL", 100, 80, PositionDriftKind.QuantityMismatch),
            Drift("MSFT", 10, 0, PositionDriftKind.LedgerOnly),
        ];
        PositionDriftItem[] reversed = [forward[1], forward[0]];

        tracker.ShouldReport(forward).Should().BeFalse();
        tracker.ShouldReport(reversed).Should().BeTrue();
    }

    [Fact]
    public void 必要な連続回数は構成できる()
    {
        var tracker = NewTracker(new InMemoryPositionDriftStateStore(), required: 3);

        tracker.ShouldReport(DriftA).Should().BeFalse();
        tracker.ShouldReport(DriftA).Should().BeFalse();
        tracker.ShouldReport(DriftA).Should().BeTrue();
    }

    [Fact]
    public void 連続回数の下限は1に丸める()
    {
        // 0・負値で「報告しない」状態を作らない（検知器が黙る設定を許さない）。
        var tracker = NewTracker(new InMemoryPositionDriftStateStore(), required: 0);

        tracker.ShouldReport(DriftA).Should().BeTrue();
    }

    // ---- #305: 複数レプリカ（競合コンシューマ）での取りこぼし ----

    [Fact]
    public void 複数レプリカに分散した観測でも連続条件が成立する()
    {
        // #305 の回帰。replicas>1 では BrokerPositionsObserved が Pod へラウンドロビンで分散する。
        // 状態がプロセス内だと各 Pod のカウンタが 1 のままで、乖離が例外もログも出さずに恒久未報告になる。
        var shared = new InMemoryPositionDriftStateStore();
        var replicaA = NewTracker(shared);
        var replicaB = NewTracker(shared);

        replicaA.ShouldReport(DriftA).Should().BeFalse();
        replicaB.ShouldReport(DriftA).Should().BeTrue("観測が分散しても連続 2 回目で報告されなければ乖離は無言で未報告のままになる");
    }

    [Fact]
    public void レプリカをまたいでも継続中の乖離は再報告しない()
    {
        var shared = new InMemoryPositionDriftStateStore();
        var replicaA = NewTracker(shared);
        var replicaB = NewTracker(shared);

        replicaA.ShouldReport(DriftA);
        replicaB.ShouldReport(DriftA).Should().BeTrue();

        // 報告済みシグネチャも共有される＝どの Pod が受けても 10 分ごとに Discord を叩かない。
        replicaA.ShouldReport(DriftA).Should().BeFalse();
        replicaB.ShouldReport(DriftA).Should().BeFalse();
    }

    [Fact]
    public void レプリカをまたいだ解消と再発でも再び報告する()
    {
        var shared = new InMemoryPositionDriftStateStore();
        var replicaA = NewTracker(shared);
        var replicaB = NewTracker(shared);

        replicaA.ShouldReport(DriftA);
        replicaB.ShouldReport(DriftA).Should().BeTrue();

        replicaA.ShouldReport([]).Should().BeFalse();

        replicaB.ShouldReport(DriftA).Should().BeFalse();
        replicaA.ShouldReport(DriftA).Should().BeTrue();
    }

    [Fact]
    public void 並行更新に負けた観測は報告しないが次の観測で報告できる()
    {
        // IADR-0124 決定 2: 負けた側は捨てる（リトライしない）。必ずどれか 1 つは勝つため状態は単調に前進し、
        // 捨てた内容は乖離が解消するまで毎巡回で再観測される＝失うのは最大 1 巡回分の時間であって報告ではない。
        var store = new LosesNthSaveStore(new InMemoryPositionDriftStateStore(), losingSaveNumber: 2);
        var tracker = NewTracker(store);

        tracker.ShouldReport(DriftA).Should().BeFalse();
        tracker.ShouldReport(DriftA).Should().BeFalse("2 回目は報告条件を満たすが並行更新に負けたので報告しない");
        tracker.ShouldReport(DriftA).Should().BeTrue("次の観測で数え直して報告へ到達する（止まらない）");
    }

    [Fact]
    public void 並行更新に負けたことはログに残す()
    {
        // 無言にしない。#305 が消そうとしている失敗の形（例外もログも出ずに黙る）を再生産しない。
        var store = new LosesNthSaveStore(new InMemoryPositionDriftStateStore(), losingSaveNumber: 1);
        var logger = new RecordingLogger<PositionDriftTracker>();

        new PositionDriftTracker(store, logger).ShouldReport(DriftA).Should().BeFalse();

        logger.Records.Should().ContainSingle()
            .Which.Level.Should().Be(LogLevel.Debug);
    }

    [Fact]
    public void 乖離ゼロが続く巡回では状態を書き換えない()
    {
        // 解消後は状態が完全に不変になる（連続回数も 0 へ戻る）ため、10 分ごとに DB を叩き続けない。
        var store = new InMemoryPositionDriftStateStore();
        var tracker = NewTracker(store);
        tracker.ShouldReport(DriftA);
        tracker.ShouldReport(DriftA).Should().BeTrue();
        tracker.ShouldReport([]);

        var settled = store.Get();
        settled.Should().Be(PositionDriftState.Initial with { Version = settled.Version });

        tracker.ShouldReport([]).Should().BeFalse();
        tracker.ShouldReport([]).Should().BeFalse();

        store.Get().Version.Should().Be(settled.Version, "変化のない巡回では版が進まない＝書き込みが起きていない");
    }

    [Fact]
    public void 未記録の状態からは数え直す()
    {
        // fail-safe の向きは「報告しない」ではなく「数え直す」側（次の連続観測で必ず報告へ到達する）。
        var store = new InMemoryPositionDriftStateStore();

        store.Get().Should().Be(PositionDriftState.Initial);

        NewTracker(store).ShouldReport(DriftA).Should().BeFalse();
        store.Get().ConsecutiveCount.Should().Be(1);
    }

    /// <summary>n 回目の保存だけ「並行更新に負けた」を返すストア（他レプリカの先行を再現する）。</summary>
    private sealed class LosesNthSaveStore(IPositionDriftStateStore inner, int losingSaveNumber)
        : IPositionDriftStateStore
    {
        private int _saves;

        public PositionDriftState Get() => inner.Get();

        public bool TrySave(PositionDriftState state)
        {
            _saves++;
            return _saves != losingSaveNumber && inner.TrySave(state);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Records.Add((logLevel, formatter(state, exception)));
    }
}
