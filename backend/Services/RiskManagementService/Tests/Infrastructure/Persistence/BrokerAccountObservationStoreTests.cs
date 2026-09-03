using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-19, FR-10, #375, ADR-0021 決定3, IADR-0153 決定3: 口座種別の観測（最新 1 件）の保持と**鮮度**。
//
// ADR-0021 は照会の頻度・キャッシュを定めていない。実装の裁量として、
//   - 観測は**永続化しない**（プロセス再起動で消える＝再起動直後は新規建てが止まり、次の probe で復帰する）
//   - 観測には**有効期限（30 分）がある**（定期 probe のクランプ上限と同値。健全な probe は必ず更新できる）
// を採る。**古い観測を無期限に信じない**ことが要点である——利用者が口座を切り替えた後も旧種別の統制で
// 回り続けると、現金口座なのに GFV 回避ガードが無効のまま回転する（決定3 が防ごうとしている事故）。
public class BrokerAccountObservationStoreTests
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 6, 6, 0, 0, TimeSpan.Zero);

    // TimeProvider の最小の偽装（FakeTimeProvider は中央パッケージ管理に未登録。IADR-0064/0066 と同じ理由）。
    private sealed class StubTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void 観測が無ければ現在値は未確定である()
    {
        var store = new InMemoryBrokerAccountObservationStore(new StubTimeProvider(Origin));

        store.GetCurrent().Should().BeNull();
    }

    [Fact]
    public void 記録した観測を現在値として返す()
    {
        var time = new StubTimeProvider(Origin);
        var store = new InMemoryBrokerAccountObservationStore(time);

        store.Record(new BrokerAccountState(AccountType.Cash, 1_000m), Origin);

        store.GetCurrent().Should().Be(new BrokerAccountState(AccountType.Cash, 1_000m));
    }

    // **鮮度（IADR-0153 決定3）**: 有効期間の境界。ちょうど 30 分は有効、超えたら失効する。
    [Theory]
    [InlineData(29, true)]
    [InlineData(30, true)]  // 境界ちょうどは有効（probe の最大間隔と同値のため、健全な巡回で必ず更新できる）
    [InlineData(31, false)] // 失効 → 「観測が無い」と同じ扱い＝新規建てが止まる
    public void 観測は有効期間を過ぎると失効する(int elapsedMinutes, bool expectedValid)
    {
        var time = new StubTimeProvider(Origin);
        var store = new InMemoryBrokerAccountObservationStore(time);
        store.Record(new BrokerAccountState(AccountType.Margin), Origin);

        time.Now = Origin.AddMinutes(elapsedMinutes);

        (store.GetCurrent() is not null).Should().Be(expectedValid);
    }

    [Fact]
    public void 有効期間は定期probeのクランプ上限と同値である() =>
        // 30 分は BrokerAvailabilityProbeOptions の IntervalSeconds クランプ上限（1800 秒）と同値である。
        // ここがずれると「設定できる最長間隔で回すと観測が常に失効している」状態が作れてしまう。
        InMemoryBrokerAccountObservationStore.MaxAge.Should().Be(TimeSpan.FromMinutes(30));

    // 再配送・順序の入れ替わりで**古い種別が新しい観測を上書きしない**（ADR-0013 / IADR-0129 決定10 の冪等性）。
    [Fact]
    public void 逆行する観測は無視する()
    {
        var time = new StubTimeProvider(Origin);
        var store = new InMemoryBrokerAccountObservationStore(time);
        store.Record(new BrokerAccountState(AccountType.Cash), Origin);

        store.Record(new BrokerAccountState(AccountType.Margin), Origin.AddMinutes(-5));

        store.GetCurrent()!.AccountType.Should().Be(AccountType.Cash);
    }

    [Fact]
    public void 新しい観測は現在値を上書きする()
    {
        var time = new StubTimeProvider(Origin);
        var store = new InMemoryBrokerAccountObservationStore(time);
        store.Record(new BrokerAccountState(AccountType.Margin), Origin);

        time.Now = Origin.AddMinutes(5);
        store.Record(new BrokerAccountState(AccountType.Cash, 500m), Origin.AddMinutes(5));

        store.GetCurrent()!.AccountType.Should().Be(AccountType.Cash);
    }

    // 同時刻の再配送は無視する（等号を含む＝同じ観測が 2 度届いても状態が動かない）。
    [Fact]
    public void 同時刻の再配送は現在値を変えない()
    {
        var time = new StubTimeProvider(Origin);
        var store = new InMemoryBrokerAccountObservationStore(time);
        store.Record(new BrokerAccountState(AccountType.Cash), Origin);

        store.Record(new BrokerAccountState(AccountType.Margin), Origin);

        store.GetCurrent()!.AccountType.Should().Be(AccountType.Cash);
    }

    // FR-19, #375, IADR-0153: 供給経路（ストア → スナップショット）。**未供給・失効は null のまま渡す**——
    // ここで既定値を補うと、判定コアの fail-closed が丸ごと無効になる。
    [Fact]
    public void スナップショットは観測された口座状態をそのまま載せる()
    {
        var time = new StubTimeProvider(Origin);
        var store = new InMemoryBrokerAccountObservationStore(time);
        var observed = new BrokerAccountState(AccountType.Cash, 1_234m);
        store.Record(observed, Origin);
        var builder = new PortfolioSnapshotBuilder(
            new FakePortfolioStateProvider(new PortfolioState { Capital = 100_000m }),
            new InMemoryKillSwitchStore(),
            new InMemoryPauseStore(),
            store,
            FakeInformationDegradation.Affirmed());

        builder.Build().Account.Should().Be(observed);
    }

    [Fact]
    public void 観測が無ければスナップショットの口座状態はnullである()
    {
        var builder = new PortfolioSnapshotBuilder(
            new FakePortfolioStateProvider(new PortfolioState { Capital = 100_000m }),
            new InMemoryKillSwitchStore(),
            new InMemoryPauseStore(),
            new InMemoryBrokerAccountObservationStore(new StubTimeProvider(Origin)),
            FakeInformationDegradation.Affirmed());

        builder.Build().Account.Should().BeNull();
    }

    [Fact]
    public void 失効した観測はスナップショットに載らない()
    {
        var time = new StubTimeProvider(Origin);
        var store = new InMemoryBrokerAccountObservationStore(time);
        store.Record(new BrokerAccountState(AccountType.Margin), Origin);
        time.Now = Origin.AddHours(2);
        var builder = new PortfolioSnapshotBuilder(
            new FakePortfolioStateProvider(new PortfolioState { Capital = 100_000m }),
            new InMemoryKillSwitchStore(),
            new InMemoryPauseStore(),
            store,
            FakeInformationDegradation.Affirmed());

        builder.Build().Account.Should().BeNull();
    }
}
