using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 NFR-09, FR-05, FR-20, ADR-0045 決定1・決定2, #1051, IADR-0444: 予約の解放の門は取引環境（SIMULATE / 実弾）ごとに分かれ、
// **予約ごとに、その予約の取引環境で**評価される。SIMULATE の門が開いていても実弾の予約は解放されない。
//
// 受け入れ基準（#1051）:
//   1. 実弾の門が閉じている間は、SIMULATE の門が開いていても、実弾の予約は解放されない（T-10-1601・否定形）。
//   2. Indeterminate は、どちらの門を開けても据え置かれる（T-10-1606）。
public class ReleaseGatePerTradingEnvironmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StalledAt = Now.AddHours(-48);
    private static readonly DateTimeOffset Cutoff = Now.AddHours(-24);

    private const BrokerProvider Simulate = BrokerProvider.MoomooSimulate;
    private const BrokerProvider Real = BrokerProvider.MoomooReal;

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class StubProbe(ReservationProbeResult result) : IReservationBrokerProbe
    {
        public Task<ReservationProbeResult> ProbeAsync(
            OrderDispatchReservation reservation, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private static ReconciliationOptions Gates(bool simulate, bool real) =>
        new() { Enabled = true, UseBrokerProbe = true, ReleaseOnNotPlaced = new() { Simulate = simulate, Real = real } };

    // 予約 1 件（取引環境 reservationProvider）を、照会先 probeProvider の口座で照会して 1 巡回回す。
    private static async Task<(ReservationReconciliationResult Result, InMemoryOrderReservationStore Reservations, Guid Id)>
        ReconcileOneAsync(
            BrokerProvider? reservationProvider,
            BrokerProvider probeProvider,
            ReconciliationOptions options,
            ReservationProbeResult? probeResult = null)
    {
        var reservations = new InMemoryOrderReservationStore();
        var id = Guid.NewGuid();
        reservations.TryReserve(id, StalledAt, reservationProvider);
        var reconciler = new OrderReservationReconciler(
            reservations, new InMemoryExecutedOrderStore(), new StubProbe(probeResult ?? ReservationProbeResult.NotPlaced),
            new ProviderOverrideBroker(probeProvider), new FakeClock(),
            Microsoft.Extensions.Options.Options.Create(options));
        var result = await reconciler.ReconcileAsync(Cutoff, batchSize: 50);
        return (result, reservations, id);
    }

    private static void AssertHeld(ReservationReconciliationResult result, InMemoryOrderReservationStore reservations, Guid id)
    {
        result.Released.Should().Be(0, "解放してはならない（再発注の許可になる）");
        result.HeldNotPlaced.Should().Equal(id);
        reservations.Find(id)!.State.Should().Be(OrderDispatchState.Reserved, "予約は据え置く");
    }

    // ---- T-10-1600: 既定 ----

    [Fact]
    public async Task 門はどちらも既定で閉じており_構成が無くても解放しない()
    {
        // T-10-1600: 既定はどちらも閉。構成そのものが無い（options=null）ときも閉じた側へ倒れる。
        var options = new ReconciliationOptions();
        options.ReleaseOnNotPlaced.Simulate.Should().BeFalse();
        options.ReleaseOnNotPlaced.Real.Should().BeFalse();

        foreach (var provider in new[] { Simulate, Real })
        {
            var reservations = new InMemoryOrderReservationStore();
            var id = Guid.NewGuid();
            reservations.TryReserve(id, StalledAt, provider);
            var reconciler = new OrderReservationReconciler(
                reservations, new InMemoryExecutedOrderStore(), new StubProbe(ReservationProbeResult.NotPlaced),
                new ProviderOverrideBroker(provider), new FakeClock(), options: null);

            AssertHeld(await reconciler.ReconcileAsync(Cutoff, 50), reservations, id);
        }
    }

    // ---- T-10-1601 / T-10-1602: 取引環境ごとの門 ----

    [Fact]
    public async Task 実弾の門が閉じている間はSIMULATEの門が開いていても実弾の予約は解放されない()
    {
        // 🔴 T-10-1601（否定形・#1051 受け入れ基準 1）: ADR-0045 決定2「SIMULATE の記録で実弾の門を開けない」。
        // 殺す変異: 門の評価で取引環境を見ず常に SIMULATE の門を使う（M1）。
        var (result, reservations, id) = await ReconcileOneAsync(Real, Real, Gates(simulate: true, real: false));

        AssertHeld(result, reservations, id);
    }

    [Fact]
    public async Task SIMULATEの門が開いていればSIMULATEの予約は解放され_実弾の門だけでは解放されない()
    {
        // T-10-1602（肯定と対照）: 門は取引環境ごとに独立して効く。
        var (opened, openedStore, openedId) = await ReconcileOneAsync(Simulate, Simulate, Gates(simulate: true, real: false));
        opened.Released.Should().Be(1);
        openedStore.Find(openedId).Should().BeNull("SIMULATE の門が開いており、予約も照会先も SIMULATE");

        var (crossed, crossedStore, crossedId) = await ReconcileOneAsync(Simulate, Simulate, Gates(simulate: false, real: true));
        AssertHeld(crossed, crossedStore, crossedId);

        // 対照: 実弾の門は実弾の予約（照会先も実弾）にだけ効く。
        var (real, realStore, realId) = await ReconcileOneAsync(Real, Real, Gates(simulate: false, real: true));
        real.Released.Should().Be(1);
        realStore.Find(realId).Should().BeNull();
    }

    // ---- T-10-1603〜T-10-1605: 解放しない側 ----

    [Theory]
    [InlineData(BrokerProvider.MoomooSimulate)]
    [InlineData(BrokerProvider.MoomooReal)]
    [InlineData(BrokerProvider.InternalPaper)]
    public async Task 取引環境が不明な予約はどちらの門を開けても解放されない(BrokerProvider probeProvider)
    {
        // 🔴 T-10-1603（否定形・原則 A）: 列を足す前の予約（null）は SIMULATE とは読まない。実弾と同じ厳しい側に倒しても、
        // 照会先と一致することを示せないため、両方の門を開けても据え置く。
        // 殺す変異: 不明を SIMULATE として扱う（M2）。
        var (result, reservations, id) = await ReconcileOneAsync(null, probeProvider, Gates(simulate: true, real: true));

        AssertHeld(result, reservations, id);
    }

    [Theory]
    [InlineData(BrokerProvider.MoomooSimulate, BrokerProvider.MoomooReal)]
    [InlineData(BrokerProvider.MoomooReal, BrokerProvider.MoomooSimulate)]
    [InlineData(BrokerProvider.MoomooSimulate, BrokerProvider.InternalPaper)]
    public async Task 予約の取引環境と照会先が食い違えば両方の門を開けても解放されない(
        BrokerProvider reservationProvider, BrokerProvider probeProvider)
    {
        // 🔴 T-10-1604（否定形）: 発注先を切り替えた後（構成の入れ替え・将来は画面の発注先）の予約を別の口座で照会して
        // 「一致ゼロ」でも、それは未発注の根拠にならない。
        // 殺す変異: 取引環境と照会先の一致を見ない（M3）。
        var (result, reservations, id) =
            await ReconcileOneAsync(reservationProvider, probeProvider, Gates(simulate: true, real: true));

        AssertHeld(result, reservations, id);
    }

    [Fact]
    public async Task 内蔵paperの予約は両方の門を開けても解放されない()
    {
        // T-10-1605（否定形）: 内蔵 paper は外部の門（SIMULATE / 実弾）の対象ではない。
        var (result, reservations, id) = await ReconcileOneAsync(
            BrokerProvider.InternalPaper, BrokerProvider.InternalPaper, Gates(simulate: true, real: true));

        AssertHeld(result, reservations, id);
    }

    // ---- T-10-1606: Indeterminate ----

    [Theory]
    [InlineData(BrokerProvider.MoomooSimulate)]
    [InlineData(BrokerProvider.MoomooReal)]
    public async Task 判定不能はどちらの門を開けても据え置かれる(BrokerProvider provider)
    {
        // 🔴 T-10-1606（否定形・#1051 受け入れ基準 2）: 門は NotPlaced にしか効かない（T-10-602 の性質の保持）。
        // 予約と照会先の取引環境が一致し、両方の門が開いていても、判定不能は解放しない。
        // 殺す変異: 門が開いていれば Indeterminate も解放する（M5）。
        var (result, reservations, id) = await ReconcileOneAsync(
            provider, provider, Gates(simulate: true, real: true), ReservationProbeResult.Indeterminate);

        result.Released.Should().Be(0);
        result.Indeterminate.Should().Be(1);
        result.HeldNotPlaced.Should().BeEmpty();
        result.Verdicts.Should().ContainSingle().Which.Should().Be(
            new ReservationReconciliationVerdict(id, provider, ReservationReconciliationVerdictKind.Indeterminate));
        reservations.Find(id)!.State.Should().Be(OrderDispatchState.Reserved);
    }

    // ---- T-10-1607 / T-10-1608: 旧キーの扱い ----

    [Theory]
    [InlineData("true")]
    [InlineData(" True ")]
    public void 旧キーのtrueはSIMULATEの門だけを開け_実弾の門は閉じたまま(string legacy)
    {
        // 🔴 T-10-1607（移行）: 旧キー Reconciliation:ReleaseOnNotPlaced は取引環境を区別しなかった。
        // SIMULATE の門にだけ写し、実弾の門には決して写さない（ADR-0045 決定2）。
        // 殺す変異: 旧キーを実弾の門にも写す（M4）。
        var options = new ReconciliationOptions();

        options.ApplyLegacyReleaseOnNotPlaced(legacy, simulateKeyPresent: false);

        options.ReleaseOnNotPlaced.Simulate.Should().BeTrue();
        options.ReleaseOnNotPlaced.Real.Should().BeFalse("旧キーは実弾の門へ写らない");
        options.LegacyReleaseOnNotPlacedMapped.Should().BeTrue("起動時に Warning を出す材料");
    }

    [Fact]
    public void 旧キーのtrueで実弾の門が開くことはない_実弾の予約は解放されない()
    {
        // T-10-1607（移行・結線）: 写像の結果をリコンサイラへ通しても、実弾の予約は据え置かれる。
        var options = Gates(simulate: false, real: false);
        options.ApplyLegacyReleaseOnNotPlaced("true", simulateKeyPresent: false);

        ReleaseGatePolicy.MayRelease(Real, Real, options.ReleaseOnNotPlaced).Should().BeFalse();
        ReleaseGatePolicy.MayRelease(Simulate, Simulate, options.ReleaseOnNotPlaced).Should().BeTrue();
    }

    [Fact]
    public void 旧キーのfalseは両方の門を閉じたままにする_稼働中の構成でnoop()
    {
        // T-10-1608（移行）: 稼働中の PoC は旧キーを "false" で持つ。写した結果は既定と同じ閉であり、何も変わらない。
        var options = new ReconciliationOptions();

        options.ApplyLegacyReleaseOnNotPlaced("false", simulateKeyPresent: false);

        options.ReleaseOnNotPlaced.Simulate.Should().BeFalse();
        options.ReleaseOnNotPlaced.Real.Should().BeFalse();
        options.LegacyReleaseOnNotPlacedMapped.Should().BeFalse();
    }

    [Theory]
    [InlineData("true", false)]
    [InlineData("false", true)]
    public void 新キーが在れば旧キーは無視され新キーが勝つ(string legacy, bool simulateFromNewKey)
    {
        // T-10-1608（移行）: 新キー …:Simulate の値（束縛済み）を旧キーで上書きしない。
        var options = new ReconciliationOptions { ReleaseOnNotPlaced = new() { Simulate = simulateFromNewKey } };

        options.ApplyLegacyReleaseOnNotPlaced(legacy, simulateKeyPresent: true);

        options.ReleaseOnNotPlaced.Simulate.Should().Be(simulateFromNewKey);
        options.ReleaseOnNotPlaced.Real.Should().BeFalse();
        options.LegacyReleaseOnNotPlacedIgnored.Should().BeTrue();
        options.LegacyReleaseOnNotPlacedMapped.Should().BeNull();
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("open")]
    public void 旧キーが真偽値でなければ起動時に止まる(string legacy)
    {
        // T-10-1608（移行・否定形）: 従前の束縛も真偽値でなければ止まった。黙って閉（または開）へ倒さない。
        var options = new ReconciliationOptions();

        var act = () => options.ApplyLegacyReleaseOnNotPlaced(legacy, simulateKeyPresent: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*ReleaseOnNotPlaced:Simulate*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void 旧キーが無ければ何もしない(string? legacy)
    {
        // 空・空白は「無い」と同じ（子を持つ節は供給元によって空文字を自分の値として返す。T-10-1609 で実測）。
        var options = new ReconciliationOptions();

        options.ApplyLegacyReleaseOnNotPlaced(legacy, simulateKeyPresent: false);

        options.ReleaseOnNotPlaced.Simulate.Should().BeFalse();
        options.LegacyReleaseOnNotPlacedMapped.Should().BeNull();
        options.LegacyReleaseOnNotPlacedIgnored.Should().BeFalse();
    }
}
