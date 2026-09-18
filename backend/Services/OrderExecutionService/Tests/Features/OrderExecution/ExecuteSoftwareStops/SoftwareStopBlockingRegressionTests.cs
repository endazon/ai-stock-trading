using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-10, FR-12, UC-02, ADR-0040 決定1（S1）, #820 の 4 巡目監査, IADR-0344 追記(4):
// 4 巡目の監査が挙げたブロッキング 4 件（BLK-1〜4）の再発防止。
//
// 🔴 **本ファイルのテストは、作り直し前のコード（配分方式）でも「コンパイルできる」形に保つ**
// ——赤→緑（是正前に落ちること）を実測するためである。新しい列（残保護数量）を直接見るテストは
// SoftwareStopExecutorTests / ProtectiveStopGuardSoftwareStopTests / EfProtectiveStopOrderStoreTests 側に置く。
public class SoftwareStopBlockingRegressionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 15, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class FakeBroker : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = [];

        public Dictionary<string, BrokerOrder> Orders { get; } = new();

        public List<(OrderIntent Intent, Guid DecisionId)> MarketCloses { get; } = [];
        public List<string> Cancelled { get; } = [];

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            throw new NotSupportedException("S1 はブローカーへ逆指値を出さない");

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            MarketCloses.Add((closeIntent, decisionId));
            return Task.FromResult(new BrokerOrder(
                $"close-{MarketCloses.Count}", closeIntent, OrderStatus.Accepted, 0, 0m, Now, null));
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult(Orders.TryGetValue(orderId, out var order) ? order : null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            Cancelled.Add(orderId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult(Positions);
    }

    private sealed record Fixture(
        SoftwareStopExecutor Executor,
        ProtectiveStopGuard Guard,
        FakeBroker Broker,
        InMemoryProtectiveStopOrderStore Stops,
        InMemoryExecutedOrderStore Store);

    private static Fixture NewFixture()
    {
        var broker = new FakeBroker();
        var stops = new InMemoryProtectiveStopOrderStore();
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var clock = new FakeClock();
        var executor = new SoftwareStopExecutor(broker, broker, stops, store, reservations, clock);
        return new Fixture(
            executor, new ProtectiveStopGuard(broker, broker, stops, store, clock, executor), broker, stops, store);
    }

    private static BrokerPositionSnapshot Long(int qty) => new("AAPL", Market.UnitedStates, qty, 1_000m);

    private static ProtectiveStopOrder SoftwareStop(DateTimeOffset createdAt, int quantity = 10, DateTimeOffset? triggeredAt = null)
    {
        var id = Guid.NewGuid();
        return new ProtectiveStopOrder(
            id, ProtectiveStopIds.SoftwareStopId(id), string.Empty, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, 950m, 1m, 0, ProtectiveStopState.Active,
            createdAt, createdAt, StopLossExecutionMethod.SoftwareStop, triggeredAt, triggeredAt is null ? null : 940m);
    }

    private static ProtectiveStopOrder BrokerStop(
        DateTimeOffset createdAt, int quantity, string stopOrderId = "stop-s0",
        ProtectiveStopState state = ProtectiveStopState.Active)
    {
        var id = Guid.NewGuid();
        return new ProtectiveStopOrder(
            id, ProtectiveStopIds.StopDecisionId(id, 1), stopOrderId, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, 900m, 1m, 1, state, createdAt, createdAt);
    }

    private static void Entry(Fixture f, ProtectiveStopOrder stop, OrderStatus status, int filled) =>
        f.Store.Save(new ExecutionRecord(
            stop.EntryDecisionId, $"entry-{stop.EntryDecisionId:N}", "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, PositionEffect.Open, stop.Quantity, 1_000m, filled, 1_000m, status, 0m, Now.AddHours(-4)));

    // ---- BLK-3: 部分的にしか決済できていない行を完了させない ----

    // T-10-372（受け入れ基準 26）: 同じ試行の決済記録が**行の残保護数量より小さい数量**で残っている
    //（発注はできたがクラッシュで行の更新を落とした窓）。行を完了させると、決済できていない残りが
    // **無保護のまま黙って残る**。残保護数量を減らすだけにとどめ、残りを次の巡回で決済する。
    [Fact]
    public async Task 部分的にしか決済できない行は完了させず残りを次の巡回で決済する()
    {
        var f = NewFixture();
        var stop = SoftwareStop(Now.AddHours(-1), quantity: 10, triggeredAt: Now);
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);
        f.Broker.Positions = [Long(10)];

        // 試行 1 の決済は 6 株だけ受理されて記録されている（行の更新だけが失われた）。
        f.Store.Save(new ExecutionRecord(
            ProtectiveStopIds.SoftwareCloseDecisionId(stop.EntryDecisionId, 1), "close-partial", "AAPL",
            Market.UnitedStates, TradeSide.Sell, ProductType.Cash, PositionEffect.Close, 6, 940m, 0, 0m,
            OrderStatus.Accepted, 0m, Now));

        var first = await f.Executor.TryCloseAsync(f.Stops.Find(stop.EntryDecisionId)!, snapshot: null);

        first.Kind.Should().NotBe(SoftwareStopCloseKind.Completed, "4 株が決済できていない");
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(
            ProtectiveStopState.Active, "完了させると残り 4 株が無保護のまま黙って残る");
        f.Broker.MarketCloses.Should().BeEmpty("記録済みの試行に重ねて発注しない");

        // 次の巡回で残りの 4 株を決済して完了する。
        f.Broker.Positions = [Long(4)];
        var second = await f.Executor.TryCloseAsync(f.Stops.Find(stop.EntryDecisionId)!, snapshot: null);

        second.Kind.Should().Be(SoftwareStopCloseKind.Completed);
        f.Broker.MarketCloses.Should().ContainSingle().Which.Intent.Quantity.Should().Be(4);
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // ---- BLK-1: 自分の建玉を失った S1 行が、生きている S0 の逆指値を取り消させる ----

    // T-10-373（受け入れ基準 27・33）: 同じ銘柄に「建玉を失った S1 の行」と「別エントリーの生きた S0 逆指値」が併存する。
    // 作り直し前は S1 行が方向の純額だけを見て**不死化**し、S0 側は S1 の主張ぶんを引いて残 0 と誤認するため、
    // **毎巡回・恒久的に生きた逆指値を黙って取り消していた**。
    //
    // 🔴 **作成順を入れ替えても成り立たなければならない**（#820 の 5 巡目監査）。割り当て順が作成時刻だけだと、
    // 古い方がたまたま S0 のときに**ブローカーに実在する生きた逆指値が 0 にされて取り消される**（無音・不可逆）。
    // 5 巡目の監査は本テストの作成時刻を入れ替えただけで同症状を再現した——あの形は不変条件ではなく
    // 「幽霊が古い」という偶然を固定していた。**帳簿だけの行（S1）を先に削る**ことで、両方の順序で成り立つ。
    [Theory]
    [InlineData(true)]   // 幽霊（S1）が古い
    [InlineData(false)]  // 生きた S0 が古い（5 巡目監査が再現した配置）
    public async Task 建玉を失ったS1の行は完了し生きているS0の逆指値を取り消させない(bool ghostIsOlder)
    {
        var f = NewFixture();
        var ghostCreatedAt = ghostIsOlder ? Now.AddHours(-3) : Now.AddHours(-1);
        var aliveCreatedAt = ghostIsOlder ? Now.AddHours(-1) : Now.AddHours(-3);
        var ghost = SoftwareStop(ghostCreatedAt, quantity: 10); // 建玉は手動決済で消えている
        var alive = BrokerStop(aliveCreatedAt, quantity: 5);    // 別エントリーの生きた S0 逆指値
        f.Stops.Save(ghost);
        f.Stops.Save(alive);
        Entry(f, ghost, OrderStatus.Filled, 10);
        f.Broker.Orders["stop-s0"] = new BrokerOrder(
            "stop-s0", new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                BrokerProvider.MoomooSimulate, 5, 900m, PositionEffect.Close), OrderStatus.Accepted, 0, 0m, Now, null);
        f.Broker.Positions = [Long(5)]; // 残っているのは S0 の 5 株だけ

        // 1 巡目: 超過 10 株は**帳簿だけの行**（幽霊）から削る。まだ確定していないので行は閉じない。
        await f.Guard.RunOnceAsync(10);

        f.Broker.Cancelled.Should().BeEmpty("生きている S0 の逆指値を取り消してはならない（建玉 5 株を守っている）");
        f.Stops.Find(ghost.EntryDecisionId)!.RemainingProtected.Should().Be(0, "幽霊の主張から削る");
        f.Stops.Find(ghost.EntryDecisionId)!.State.Should().Be(
            ProtectiveStopState.Active, "1 回の観測では閉じない（建玉照会は 1 巡回だけ過少に返り得る）");

        // 2 巡目: 超過が続いたので確定し、幽霊だけが役目を終える。
        await f.Guard.RunOnceAsync(10);

        f.Broker.Cancelled.Should().BeEmpty();
        f.Stops.Find(alive.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
        f.Stops.Find(ghost.EntryDecisionId)!.State.Should().Be(
            ProtectiveStopState.Completed, "建玉を失った行は不死化せず、確定した割り当てで役目を終える");

        // 次の巡回でも取り消さない（作り直し前は毎巡回・恒久的に取り消していた）。
        await f.Guard.RunOnceAsync(10);
        f.Broker.Cancelled.Should().BeEmpty();
    }

    // ---- BLK-2: 完了済み S0 行の取消済みレグ記録が S1 の持ち分を食う ----

    // T-10-374（受け入れ基準 28）: 作り直し前は「建玉照会がまだ映していない決済」を発注記録から数えており、
    // **完了した S0 行のレグ記録**（取り消された注文が当日一覧から消えるブローカーでは終端化できず Accepted のまま残る）が
    // 除外集合に入らず、S1 の持ち分を丸ごと削っていた（損切りが 1 株も出ない）。
    // 残保護数量方式は持ち分の計算に発注記録を**一切使わない**ため、この経路が構造的に消える。
    [Fact]
    public async Task 完了済みS0の取消済みレグ記録があってもS1の持ち分は削られない()
    {
        var f = NewFixture();
        var stop = SoftwareStop(Now.AddHours(-1), quantity: 10, triggeredAt: Now);
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);

        // 別エントリーの S0 は既に完了（建玉消滅で逆指値を取り消した）が、レグ記録は Accepted のまま残っている。
        var done = BrokerStop(Now.AddHours(-2), quantity: 10, stopOrderId: "stop-done", state: ProtectiveStopState.Completed);
        f.Stops.Save(done);
        f.Store.Save(new ExecutionRecord(
            done.StopDecisionId, "stop-done", "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            PositionEffect.Close, 10, 900m, 0, 0m, OrderStatus.Accepted, 0m, Now));

        f.Broker.Positions = [Long(10)]; // 残っているのは S1 の 10 株だけ

        var outcome = await f.Executor.TryCloseAsync(f.Stops.Find(stop.EntryDecisionId)!, snapshot: null);

        outcome.Kind.Should().Be(SoftwareStopCloseKind.Completed);
        f.Broker.MarketCloses.Should().ContainSingle()
            .Which.Intent.Quantity.Should().Be(10, "完了済み S0 のレグ記録は持ち分と無関係である");
    }

    // ---- BLK-4: 未到達の行が到達済みの行より先に持ち分を取る ----

    // T-10-375（受け入れ基準 29）: 同じ銘柄に「未到達の古い行」と「到達済みの新しい行」があり、建玉が両方を賄えない。
    // 作り直し前は配分が作成順だったため、**未到達の行が全量を取り、到達した行の決済が 1 株も出なかった**
    //（しかも据え置きのまま無音）。残保護数量方式では減少分を古い行から一度だけ割り当て、
    // ガードは**到達済みの行を未到達の行より先に**処理するため、到達した損切りが必ず出る。
    [Fact]
    public async Task 到達済みの行を未到達の行より先に処理する()
    {
        var f = NewFixture();
        var unreached = SoftwareStop(Now.AddHours(-3), quantity: 10);
        var reached = SoftwareStop(Now.AddHours(-1), quantity: 10, triggeredAt: Now);
        f.Stops.Save(unreached);
        f.Stops.Save(reached);
        Entry(f, unreached, OrderStatus.Filled, 10);
        Entry(f, reached, OrderStatus.Filled, 10);
        f.Broker.Positions = [Long(10)]; // 20 株のうち 10 株が外部で消えている

        await f.Guard.RunOnceAsync(10);

        f.Broker.MarketCloses.Should().ContainSingle("到達した行の損切りが出る")
            .Which.Intent.Quantity.Should().Be(10);
        f.Broker.MarketCloses[0].DecisionId.Should().Be(
            ProtectiveStopIds.SoftwareCloseDecisionId(reached.EntryDecisionId, 1));
        f.Stops.Find(reached.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // ---- 5 巡目監査①: 外部要因の割り当ては「帳簿だけの行」から ----

    // T-10-379（受け入れ基準 33）: S0 と S1 が**同数**を主張し、建玉が S0 の分しか無い。境界は同数にある（3 巡目監査 B3 と同じ教訓）。
    // 作成時刻だけで順序を決めると、古い S0 の主張が丸ごと削られ**生きた逆指値が取り消される**。
    // 帳簿だけの行（S1）が超過を丸ごと吸収するため、実注文を持つ行は無傷で残る。
    [Fact]
    public async Task 超過はまず帳簿だけの行から削られ生きたS0は無傷で残る()
    {
        var f = NewFixture();
        var alive = BrokerStop(Now.AddHours(-3), quantity: 10); // 生きた S0（古い）
        var software = SoftwareStop(Now.AddHours(-1), quantity: 10);
        f.Stops.Save(alive);
        f.Stops.Save(software);
        Entry(f, software, OrderStatus.Filled, 10);
        f.Broker.Orders["stop-s0"] = new BrokerOrder(
            "stop-s0", new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                BrokerProvider.MoomooSimulate, 10, 900m, PositionEffect.Close), OrderStatus.Accepted, 0, 0m, Now, null);
        f.Broker.Positions = [Long(10)]; // 残っているのは S0 の 10 株だけ

        await f.Guard.RunOnceAsync(10);
        await f.Guard.RunOnceAsync(10);

        f.Broker.Cancelled.Should().BeEmpty("実注文を持つ行は最後に回す（照会が Pending＝その建玉はまだ在る証拠）");
        f.Stops.Find(alive.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
        f.Stops.Find(alive.EntryDecisionId)!.ProtectedQuantity.Should().Be(10, "S0 の主張は削らない");
        f.Stops.Find(software.EntryDecisionId)!.State.Should().Be(
            ProtectiveStopState.Completed, "帳簿だけの行が超過を吸収して役目を終える");
    }

    // ---- 5 巡目監査②: 1 巡回だけ過少に見えた建玉照会で行を失わない ----

    // T-10-380（受け入れ基準 34）: 建玉 20 株・行 2 件に対し、**1 巡回だけ**照会が 10 株を返す。
    // 作り直し（`cf573a58` の次）では超過を削るだけで復元経路が無く、その巡回で 0 になった行が `Completed` になり、
    // **次の巡回で建玉が回復しても戻らなかった**（監査の実測: 10 株が永久に無保護・しかも無音）。
    // 削りは 2 巡回連続で観測してから確定し、建玉が戻れば**未確定の削りを復元する**。
    [Fact]
    public async Task 建玉照会が一巡回だけ過少でも行は失われず次の巡回で復元する()
    {
        var f = NewFixture();
        var first = SoftwareStop(Now.AddHours(-2), quantity: 10);
        var second = SoftwareStop(Now.AddHours(-1), quantity: 10);
        f.Stops.Save(first);
        f.Stops.Save(second);
        Entry(f, first, OrderStatus.Filled, 10);
        Entry(f, second, OrderStatus.Filled, 10);

        // 巡回 1: 照会が過少に返る（実際の建玉は 20 株）。
        f.Broker.Positions = [Long(10)];
        await f.Guard.RunOnceAsync(10);

        f.Stops.Find(first.EntryDecisionId)!.State.Should().Be(
            ProtectiveStopState.Active, "1 回の観測で行を閉じない（閉じると回復しても戻せない）");

        // 巡回 2: 照会が回復する。削った分を返す。
        f.Broker.Positions = [Long(20)];
        await f.Guard.RunOnceAsync(10);

        var restored = f.Stops.Find(first.EntryDecisionId)!;
        restored.State.Should().Be(ProtectiveStopState.Active);
        restored.RemainingProtected.Should().Be(10, "一時的なズレを恒久的なズレにしない");
        f.Stops.Find(second.EntryDecisionId)!.RemainingProtected.Should().Be(10);
        f.Broker.MarketCloses.Should().BeEmpty("未到達の行は決済しない");
    }

    // T-10-381（受け入れ基準 35）: S1 が吸収しきれない超過は S0 も削るが、**確定するまで逆指値を取り消さない**。
    // 生きた注文の取消は無音かつ不可逆であり、1 巡回だけ過少に見えた照会でそれを行ってはならない。
    [Fact]
    public async Task 未確定の外部要因があるあいだは生きたS0の逆指値を取り消さない()
    {
        var f = NewFixture();
        var alive = BrokerStop(Now.AddHours(-3), quantity: 10);
        var software = SoftwareStop(Now.AddHours(-1), quantity: 5);
        f.Stops.Save(alive);
        f.Stops.Save(software);
        Entry(f, software, OrderStatus.Filled, 5);
        f.Broker.Orders["stop-s0"] = new BrokerOrder(
            "stop-s0", new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                BrokerProvider.MoomooSimulate, 10, 900m, PositionEffect.Close), OrderStatus.Accepted, 0, 0m, Now, null);
        f.Broker.Positions = []; // 建玉が丸ごと消えて見える

        await f.Guard.RunOnceAsync(10);

        f.Broker.Cancelled.Should().BeEmpty("未確定の観測 1 回で生きた逆指値を取り消さない");
        f.Stops.Find(alive.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);

        // 2 巡目で確定し、はじめて取り消す（#826 項目 3 は保たれる）。
        await f.Guard.RunOnceAsync(10);

        f.Broker.Cancelled.Should().ContainSingle().Which.Should().Be("stop-s0");
        f.Stops.Find(alive.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        f.Stops.Find(software.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // T-10-382（受け入れ基準 36）: 外部要因で保護対象を減らしたことを**必ず 1 回**残す。
    // 作り直し後は削るのが完全に無音で、S0 の行を 0 にして逆指値を取り消す場合ですらイベントも Critical も出なかった。
    [Fact]
    public async Task 外部要因で保護対象を減らしたら一度だけ通知する()
    {
        var f = NewFixture();
        var first = SoftwareStop(Now.AddHours(-2), quantity: 10);
        var second = SoftwareStop(Now.AddHours(-1), quantity: 10);
        f.Stops.Save(first);
        f.Stops.Save(second);
        Entry(f, first, OrderStatus.Filled, 10);
        Entry(f, second, OrderStatus.Filled, 10);
        f.Broker.Positions = [Long(10)]; // 10 株が外部で消えたまま戻らない

        var cycle1 = await f.Guard.RunOnceAsync(10);
        cycle1.Events.OfType<SoftwareStopExecuted>().Should().BeEmpty("確定前に通知しない");

        var cycle2 = await f.Guard.RunOnceAsync(10);

        var notified = cycle2.Events.OfType<SoftwareStopExecuted>().Should().ContainSingle().Which;
        ((int)notified.Outcome).Should().Be(5, "SoftwareStopOutcome.ProtectionReduced（末尾へ追加した序数）");
        notified.EntryDecisionId.Should().Be(first.EntryDecisionId);
        notified.Quantity.Should().Be(10, "削った株数を残す");
        notified.CloseDecisionId.Should().BeNull("決済は出していない");

        var cycle3 = await f.Guard.RunOnceAsync(10);
        cycle3.Events.OfType<SoftwareStopExecuted>().Should().BeEmpty("同じ削りを二度通知しない");
    }

    // ---- 6 巡目監査: 復元が売り過ぎ（反対建玉）を再導入していた ----

    // T-10-384（受け入れ基準 38）: 同一銘柄・同方向の**到達済み S1 行 2 件**に対し、外部要因で建玉が半分になる。
    // 是正前の連鎖:
    //   巡回 1: Reduce が古い行 A を 0 に削る（未確定）。行 B は 10 株の成行決済を発注（受理・未約定）。
    //           Settle が `remaining == 0` で **B を Completed** にする → 次巡回の FindActive から消える。
    //   巡回 2: 建玉照会は**まだ 10 株を返す**（受理済み・未約定なので当然）。Active 行の主張の合計は 0 なので
    //           `excess = -10` となり、**Restore が A へ 10 株を返す** → 到達済みの A がさらに 10 株を決済する。
    // 合計 20 株（保有 10 株）＝**10 株の空売り**という不可逆な事故になる。
    // 復元の門は「送信済みで建玉照会に未反映の決済」を差し引くため、この巡回では復元しない。
    [Fact]
    public async Task 送信済みで未約定の決済がある銘柄では復元せず同じ建玉を二度売らない()
    {
        var f = NewFixture();
        var older = SoftwareStop(Now.AddHours(-3), quantity: 10, triggeredAt: Now);
        var newer = SoftwareStop(Now.AddHours(-1), quantity: 10, triggeredAt: Now);
        f.Stops.Save(older);
        f.Stops.Save(newer);
        Entry(f, older, OrderStatus.Filled, 10);
        Entry(f, newer, OrderStatus.Filled, 10);
        f.Broker.Positions = [Long(10)]; // 20 株のうち 10 株が外部で消えた

        // 巡回 1: 超過 10 株は古い行から削られ（未確定）、新しい行が 10 株を決済して完了する。
        await f.Guard.RunOnceAsync(10);

        f.Broker.MarketCloses.Should().ContainSingle().Which.Intent.Quantity.Should().Be(10);
        f.Stops.Find(newer.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);

        // 巡回 2: 決済は受理済み・未約定なので、建玉照会はまだ 10 株を返す。
        // **これは「建玉が戻った」ではない。** 復元すると同じ 10 株をもう一度売ることになる。
        await f.Guard.RunOnceAsync(10);

        var total = f.Broker.MarketCloses.Sum(c => c.Intent.Quantity);
        total.Should().BeLessThanOrEqualTo(10,
            "建玉は 10 株。1 巡目 10 株、累計 20 株を売ると反対建玉（空売り）になる");
        f.Stops.Find(older.EntryDecisionId)!.RemainingProtected.Should().Be(
            0, "送信済みで未反映の決済は「戻ってきた建玉」ではない");
        f.Stops.Find(older.EntryDecisionId)!.State.Should().Be(
            ProtectiveStopState.Completed, "2 巡回目の観測で削りが確定し、役目を終える");
    }

    // T-10-385（受け入れ基準 39）: 幽霊行の削りが**未確定のあいだ**に、同じ銘柄・同方向で
    // **新しいエントリーが約定**する。その発注記録がまだ終端でないと行の主張は 0（`RemainingProtected` が null）なので、
    // 是正前は `excess < 0` になって **Restore が幽霊行を復活させ**、新しい建玉を
    // **その行の損切りラインとは無関係に成行決済**していた（到達していない建玉を売る）。
    [Fact]
    public async Task 確定前の新規エントリーの建玉で幽霊行を復活させない()
    {
        var f = NewFixture();
        var ghost = SoftwareStop(Now.AddHours(-3), quantity: 10, triggeredAt: Now);
        f.Stops.Save(ghost);
        Entry(f, ghost, OrderStatus.Filled, 10);
        f.Broker.Positions = []; // 建玉は外部で消えている

        // 巡回 1: 主張 10 株を削る（未確定なので、まだ完了させない・決済も出さない）。
        await f.Guard.RunOnceAsync(10);

        f.Broker.MarketCloses.Should().BeEmpty("建玉が無いので決済しない");
        f.Stops.Find(ghost.EntryDecisionId)!.RemainingProtected.Should().Be(0);

        // 新しいエントリーが約定したが、発注記録はまだ終端でない（＝主張 0 のまま）。建玉照会は 10 株を返す。
        var fresh = SoftwareStop(Now, quantity: 10);
        f.Stops.Save(fresh);
        Entry(f, fresh, OrderStatus.Accepted, 10);
        f.Broker.Positions = [Long(10)];

        await f.Guard.RunOnceAsync(10);

        f.Broker.MarketCloses.Should().BeEmpty(
            "確定前の新規エントリーの建玉を、幽霊行の損切りラインで成行決済してはならない");
        f.Stops.Find(ghost.EntryDecisionId)!.State.Should().Be(
            ProtectiveStopState.Completed, "幽霊行は復活せず、2 巡回目の観測で確定して役目を終える");
        var current = f.Stops.Find(fresh.EntryDecisionId)!;
        current.State.Should().Be(ProtectiveStopState.Active);
        current.RemainingProtected.Should().BeNull("エントリーの発注記録がまだ終端でない");
    }

    // T-10-386（受け入れ基準 40）: 未確定の削りを抱えた行へ、次の巡回で**さらなる減少（増分）**が積まれる。
    // 是正前は `Reduce` が `PendingExternalReduction` へ加算するだけで観測回数をリセットせず、
    // `Confirm` は行単位の観測回数で判定していたため、**増分は 1 回しか観測していないのに**まとめて確定・通知された
    //（「2 巡回連続で観測してから確定する」が増分について破れる）。
    [Fact]
    public async Task 未確定の削りに増分が積まれたら観測を数え直す()
    {
        var f = NewFixture();
        var stop = SoftwareStop(Now.AddHours(-2), quantity: 10);
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);

        // 巡回 1: 5 株が外部で消える（未確定・観測 1 回目）。
        f.Broker.Positions = [Long(5)];
        var cycle1 = await f.Guard.RunOnceAsync(10);
        cycle1.Events.OfType<SoftwareStopExecuted>().Should().BeEmpty("観測 1 回では確定しない");

        // 巡回 2: さらに 3 株減る（真の追加減少）。この増分はまだ 1 回しか観測していない。
        f.Broker.Positions = [Long(2)];
        var cycle2 = await f.Guard.RunOnceAsync(10);

        cycle2.Events.OfType<SoftwareStopExecuted>().Should().BeEmpty(
            "増分は 1 回しか観測していない。行全体の観測回数で確定してはならない");
        var pending = f.Stops.Find(stop.EntryDecisionId)!;
        pending.PendingExternalReduction.Should().Be(8, "減算そのものは即時に行う（5 ＋ 3）");
        pending.ExternalReductionObservations.Should().Be(1, "増分が入った巡回で観測を数え直す");

        // 巡回 3: 超過が続いたのではじめて確定し、1 回だけ通知する。
        var cycle3 = await f.Guard.RunOnceAsync(10);

        var notified = cycle3.Events.OfType<SoftwareStopExecuted>().Should().ContainSingle().Which;
        ((int)notified.Outcome).Should().Be(5, "SoftwareStopOutcome.ProtectionReduced");
        notified.Quantity.Should().Be(8);
    }
}
