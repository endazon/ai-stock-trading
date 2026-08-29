using RiskManagementService.Infrastructure.Persistence;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-01, FR-02, FR-10, ADR-0020 決定2/決定3, #564, IADR-0267:
// 情報収集の縮退ストアの**鮮度**（観測の有効期間）と、**既定の向き**の検証。
//
// 🔴 **本件の中核は「不明なら通す」を「不明なら止める」へ倒すことである。**
// 復元経路（毎巡回の現況観測）を足しても、既定が fail-open のままなら統制は再起動 1 回で解ける。
//
// テスト戦略（docs/tests/README.md §2）の 3 点セットで構成する。
//   1. 境界値 — 有効期間ちょうど / 超過、クランプの上下限
//   2. プロパティベース — (観測の有無) × (失効) × (停止カテゴリ) の全 8 通りで成り立つ不変条件
//   3. 否定形 — 未観測・失効・逆行観測・遷移だけでは鮮度が戻らないこと（対の肯定形を添える）
public class InformationDegradationStoreFreshnessTests
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 29, 6, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Validity = TimeSpan.FromHours(1);

    private static (InMemoryInformationDegradationStore Store, StubTimeProvider Time) Create()
    {
        var time = new StubTimeProvider(Origin);
        return (new InMemoryInformationDegradationStore(time), time);
    }

    // ------------------------------------------------------------------
    // 1. 境界値
    // ------------------------------------------------------------------

    // 有効期間ちょうどは有効、超えたら失効する（BrokerAccountObservationStoreTests と同じ形）。
    [Theory]
    [InlineData(59, false)]
    [InlineData(60, false)] // 境界ちょうどは有効（＝止めない）
    [InlineData(61, true)]  // 失効 → 「観測が無い」と同じ扱い＝新規建てが止まる
    public void 観測は有効期間を過ぎると失効する(int elapsedMinutes, bool expectedBlocked)
    {
        var (store, time) = Create();
        store.ApplyObservation([], Validity, Origin);

        time.Now = Origin.AddMinutes(elapsedMinutes);

        store.BlocksNewEntries.Should().Be(expectedBlocked);
    }

    // クランプの境界。発行側の宣言をそのまま信じない（上限を超える宣言で鮮度の要求が消えない）。
    [Theory]
    [InlineData(0, 1)]       // 0 や負値は下限へ（常時停止に落ちない）
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(60, 60)]
    [InlineData(120, 120)]   // 上限ちょうど
    [InlineData(1_000, 120)] // 上限超は切り詰める
    public void 有効期間は上下限へクランプされる(int declaredMinutes, int expectedMinutes)
    {
        InMemoryInformationDegradationStore.Clamp(TimeSpan.FromMinutes(declaredMinutes))
            .Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    // 🔴 クランプが**実際に判定へ効く**ことまで見る（純関数だけ試すと配線漏れが素通りする）。
    [Fact]
    public void 上限を超える有効期間を宣言しても上限で失効する()
    {
        var (store, time) = Create();
        store.ApplyObservation([], TimeSpan.FromDays(30), Origin);

        time.Now = Origin.AddHours(2);
        store.BlocksNewEntries.Should().BeFalse("上限ちょうどは有効");

        time.Now = Origin.AddHours(2).AddMinutes(1);
        store.BlocksNewEntries.Should().BeTrue("宣言が長くても上限で失効する");
    }

    // ------------------------------------------------------------------
    // 2. プロパティベース（8 通りで常に成り立つ不変条件）
    // ------------------------------------------------------------------

    public static TheoryData<bool, bool, bool> AllCombinations()
    {
        var data = new TheoryData<bool, bool, bool>();
        foreach (var observed in new[] { false, true })
        {
            foreach (var expired in new[] { false, true })
            {
                foreach (var blocking in new[] { false, true })
                {
                    data.Add(observed, expired, blocking);
                }
            }
        }

        return data;
    }

    // 🔴 不変条件: **新規建てを通してよいのは「有効な観測 ∧ 停止カテゴリなし」のときだけ**である。
    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void 新規建てが通るのは有効な観測が止めるものは無いと言うときだけ(bool observed, bool expired, bool blocking)
    {
        var (store, time) = Create();
        if (observed)
        {
            store.ApplyObservation(blocking ? ["news"] : [], Validity, Origin);
        }

        time.Now = expired ? Origin.Add(Validity).AddMinutes(1) : Origin;

        var fresh = observed && !expired;
        store.BlocksNewEntries.Should().Be(!(fresh && !blocking));
    }

    // ------------------------------------------------------------------
    // 3. 否定形（対の肯定形つき）
    // ------------------------------------------------------------------

    // 🔴 再起動直後は「縮退の記録が無い」が「健全」を意味しない。**不明は止める。**
    [Fact]
    public void 観測を一度も受け取っていなければ新規建てを止める_否定形()
    {
        var (store, _) = Create();

        store.BlocksNewEntries.Should().BeTrue();
    }

    [Fact]
    public void 健全な観測を受け取れば新規建ては通る_対の肯定形()
    {
        var (store, _) = Create();

        store.ApplyObservation([], Validity, Origin);

        store.BlocksNewEntries.Should().BeFalse();
    }

    // 🔴 **遷移は鮮度を与えない。** 1 件の回復は「他のカテゴリも健全である」ことを保証しないため、
    // 集合が空になっても観測が無ければ止まったままである。
    [Fact]
    public void 遷移だけでは鮮度は回復しない_否定形()
    {
        var (store, _) = Create();

        store.MarkDegraded("news");
        store.MarkRecovered("news");

        store.BlocksNewEntries.Should().BeTrue("集合は空になったが現況は依然として不明である");
    }

    [Fact]
    public void 観測のあとに来た遷移は即時に効く_対の肯定形()
    {
        var (store, _) = Create();
        store.ApplyObservation([], Validity, Origin);

        store.MarkDegraded("news");
        store.BlocksNewEntries.Should().BeTrue("次の巡回を待たずに止まる");

        store.MarkRecovered("news");
        store.BlocksNewEntries.Should().BeFalse("有効な観測が残っているので解ける");
    }

    // 🔴 逆行する観測（再配送・順序の入れ替わり）は、古い現況で新しい状態を上書きしない。
    [Fact]
    public void 逆行する観測は無視される_否定形()
    {
        var (store, _) = Create();
        store.ApplyObservation(["news"], Validity, Origin);

        store.ApplyObservation([], Validity, Origin.AddMinutes(-30)); // 古い「健全」が遅れて届く

        store.BlocksNewEntries.Should().BeTrue();
    }

    [Fact]
    public void 前進する観測は適用される_対の肯定形()
    {
        var (store, _) = Create();
        store.ApplyObservation(["news"], Validity, Origin);

        store.ApplyObservation([], Validity, Origin.AddMinutes(30));

        store.BlocksNewEntries.Should().BeFalse();
    }

    // 現況観測は**全量**である（差分ではない）。前回あって今回無いカテゴリは消える。
    [Fact]
    public void 現況観測は停止カテゴリを全量で置き換える()
    {
        var (store, _) = Create();
        store.ApplyObservation(["news", "disclosure-us"], Validity, Origin);

        store.ApplyObservation(["news"], Validity, Origin.AddMinutes(30));
        store.BlocksNewEntries.Should().BeTrue("news はまだ止まっている");

        store.ApplyObservation([], Validity, Origin.AddMinutes(60));
        store.BlocksNewEntries.Should().BeFalse();
    }

    // 空白のカテゴリ名は受け付けない（集合に「見えない停止」を作らない）。
    [Fact]
    public void 空白のカテゴリを含む観測は拒否される_否定形()
    {
        var (store, _) = Create();

        var act = () => store.ApplyObservation(["news", "  "], Validity, Origin);

        act.Should().Throw<ArgumentException>();
    }
}
