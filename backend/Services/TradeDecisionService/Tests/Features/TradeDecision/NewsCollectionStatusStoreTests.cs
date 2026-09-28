using TradeDecisionService.Features.TradeDecision;
using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-04, ADR-0020 決定2, #1081, IADR-0453: ニュースの状態の最新値を有効期限つきで保持する。
// 🔴 不明が既定: 未受信・期限切れ・旧イベント（null）・範囲外の値はすべて「不明」（null）。最後に聞いた値を信じ続けない。
public class NewsCollectionStatusStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 未受信は不明()
    {
        new NewsCollectionStatusStore().Current(T0).Should().BeNull();
    }

    [Theory]
    [InlineData(NewsCollectionStatus.Fetched)]
    [InlineData(NewsCollectionStatus.Outage)]
    [InlineData(NewsCollectionStatus.NotConfigured)]
    public void 有効期間内は最新の状態を返す(NewsCollectionStatus status)
    {
        var store = new NewsCollectionStatusStore();
        store.Record(status, TimeSpan.FromMinutes(60), T0);

        store.Current(T0).Should().Be(status);
        store.Current(T0.AddMinutes(60)).Should().Be(status, "境界（ちょうど有効期間）はまだ有効");
    }

    // 🔴 期限切れは最後の値ではなく「不明」。
    [Fact]
    public void 有効期間を過ぎたら不明()
    {
        var store = new NewsCollectionStatusStore();
        store.Record(NewsCollectionStatus.Fetched, TimeSpan.FromMinutes(60), T0);

        store.Current(T0.AddMinutes(60).AddTicks(1)).Should().BeNull();
    }

    // 旧イベント（新項目 null）は「不明」を最新の観測として記録する（前の値を残さない）。有効期間 null は下限で数える。
    [Fact]
    public void 旧イベントの状態nullは前の値を上書きして不明にする()
    {
        var store = new NewsCollectionStatusStore();
        store.Record(NewsCollectionStatus.Fetched, TimeSpan.FromMinutes(60), T0);

        store.Record(status: null, validFor: null, T0.AddMinutes(1));

        store.Current(T0.AddMinutes(2)).Should().BeNull();
    }

    [Fact]
    public void 有効期間の宣言が無ければ下限で数える()
    {
        var store = new NewsCollectionStatusStore();
        store.Record(NewsCollectionStatus.Outage, validFor: null, T0);

        store.Current(T0 + NewsCollectionStatusStore.MinValidity).Should().Be(NewsCollectionStatus.Outage);
        store.Current(T0 + NewsCollectionStatusStore.MinValidity + TimeSpan.FromTicks(1)).Should().BeNull();
    }

    // 範囲外の値（既定値 0・未知の新しい値）は「取得済み」へ倒さず「不明」。
    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void 範囲外の値は不明(int raw)
    {
        var store = new NewsCollectionStatusStore();
        store.Record((NewsCollectionStatus)raw, TimeSpan.FromMinutes(60), T0);

        store.Current(T0).Should().BeNull();
    }

    // 再配送・順序の入れ替わりで古い観測が新しい観測を上書きしない。
    [Fact]
    public void 古い観測は無視する()
    {
        var store = new NewsCollectionStatusStore();
        store.Record(NewsCollectionStatus.Outage, TimeSpan.FromMinutes(60), T0);

        store.Record(NewsCollectionStatus.Fetched, TimeSpan.FromMinutes(60), T0.AddMinutes(-5));

        store.Current(T0).Should().Be(NewsCollectionStatus.Outage);
    }

    [Fact]
    public void 有効期間は上下限へクランプする()
    {
        NewsCollectionStatusStore.Clamp(TimeSpan.Zero).Should().Be(NewsCollectionStatusStore.MinValidity);
        NewsCollectionStatusStore.Clamp(TimeSpan.FromDays(3)).Should().Be(NewsCollectionStatusStore.MaxValidity);
        NewsCollectionStatusStore.Clamp(TimeSpan.FromMinutes(60)).Should().Be(TimeSpan.FromMinutes(60));

        var store = new NewsCollectionStatusStore();
        store.Record(NewsCollectionStatus.Fetched, TimeSpan.FromDays(3), T0);
        store.Current(T0 + NewsCollectionStatusStore.MaxValidity + TimeSpan.FromTicks(1)).Should().BeNull(
            "発行側の極端に長い宣言で古い状態を信じ続けない");
    }
}
