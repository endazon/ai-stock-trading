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
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

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

    // #820 の 8 巡目監査: 猶予（実効 0 が続く行の Critical）を跨ぐ検証のため、時刻を進められるようにする。
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
    }

    private sealed class FakeBroker : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = [];

        public Dictionary<string, BrokerOrder> Orders { get; } = new();

        public List<(OrderIntent Intent, Guid DecisionId)> MarketCloses { get; } = [];
        public List<string> Cancelled { get; } = [];

        // #820 の 8 巡目監査: 武装の前提条件（帰属不明の建玉が無いこと）を確かめるため、エントリーの発注も受ける。
        public List<OrderIntent> Entries { get; } = [];

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            Entries.Add(intent);
            return Task.FromResult(new BrokerOrder(
                $"entry-{Entries.Count}", intent, OrderStatus.Filled, intent.Quantity, intent.Price, Now, Now));
        }

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
        AppSvc Execution,
        FakeClock Clock,
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
            executor, new ProtectiveStopGuard(broker, broker, stops, store, clock, executor),
            new AppSvc(broker, store, reservations, clock, stops), clock, broker, stops, store);
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

        // 1 巡目: 超過 10 株は**帳簿だけの行**（幽霊）へ観測として記録する。まだ確定していないので帳簿も State も動かない。
        await f.Guard.RunOnceAsync(10);

        f.Broker.Cancelled.Should().BeEmpty("生きている S0 の逆指値を取り消してはならない（建玉 5 株を守っている）");
        f.Stops.Find(ghost.EntryDecisionId)!.PendingExternalReduction.Should().Be(10, "幽霊の主張から削る");
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

    // T-10-380（受け入れ基準 34。#820 の 7 巡目監査 / IADR-0344 追記(7) で言い方を訂正）:
    // 建玉 20 株・行 2 件に対し、**1 巡回だけ**照会が 10 株を返す。
    // 作り直し（`cf573a58` の次）では超過を削るだけで復元経路が無く、その巡回で 0 になった行が `Completed` になり、
    // **次の巡回で建玉が回復しても戻らなかった**（監査の実測: 10 株が永久に無保護・しかも無音）。
    // 追記(7) では**帳簿を確定まで書き換えない**ので、戻す（復元する）ものがそもそも無い——
    // 行も残保護数量もそのまま残り、超過が消えた巡回は観測の連続回数が 0 へ戻るだけである。
    [Fact]
    public async Task 建玉照会が一巡回だけ過少でも行も主張も失われない()
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

        // 巡回 2: 照会が回復する。確定していないので帳簿は一度も動いておらず、戻すものが無い。
        f.Broker.Positions = [Long(20)];
        await f.Guard.RunOnceAsync(10);

        var kept = f.Stops.Find(first.EntryDecisionId)!;
        kept.State.Should().Be(ProtectiveStopState.Active);
        kept.RemainingProtected.Should().Be(10, "一時的なズレを恒久的なズレにしない");
        kept.ExternalReductionObservations.Should().Be(0, "超過が消えたら観測の連続回数を 0 へ戻す");
        f.Stops.Find(second.EntryDecisionId)!.RemainingProtected.Should().Be(10);
        f.Broker.MarketCloses.Should().BeEmpty("未到達の行は決済しない");

        // 🔴 #820 の 8 巡目監査, IADR-0344 追記(8): **帳簿と状態だけを見ても足りない。**
        // 追記(7) では観測値が単調だったため、ここで `PendingExternalReduction` が 10 のまま残り、
        // 行は Active・帳簿も 10 のままなのに**到達しても 1 株も決済しない**（無音）。
        // 超過が 2 巡回連続で消えたら観測を失効させ、**実際に建玉 20 株の全量に決済が出る**ところまで確かめる。
        await f.Guard.RunOnceAsync(10);

        f.Stops.Find(first.EntryDecisionId)!.EffectiveProtectedQuantity.Should().Be(10);

        await f.Executor.OnTriggeredAsync(new StopLossTriggered(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, 20, 940m, 950m, Now));

        f.Broker.MarketCloses.Sum(c => c.Intent.Quantity).Should().Be(20, "到達したら建玉の全量に決済が出る");
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

    // ---- 6 巡目監査: 復元が売り過ぎ（反対建玉）を再導入していた（7 巡目で復元そのものを撤去した） ----

    // T-10-384（受け入れ基準 38）: 同一銘柄・同方向の**到達済み S1 行 2 件**に対し、外部要因で建玉が半分になる。
    // 是正前の連鎖:
    //   巡回 1: Reduce が古い行 A を 0 に削る（未確定）。行 B は 10 株の成行決済を発注（受理・未約定）。
    //           Settle が `remaining == 0` で **B を Completed** にする → 次巡回の FindActive から消える。
    //   巡回 2: 建玉照会は**まだ 10 株を返す**（受理済み・未約定なので当然）。Active 行の主張の合計は 0 なので
    //           `excess = -10` となり、**Restore が A へ 10 株を返す** → 到達済みの A がさらに 10 株を決済する。
    // 合計 20 株（保有 10 株）＝**10 株の空売り**という不可逆な事故になる。
    // 🔴 追記(7): 復元という操作そのものを撤去した。この巡回で見るのは「観測を数え続けてよいか」であり、
    // 「送信済みで建玉照会に未反映の決済」が超過の消失を説明するので、観測は続いて 2 巡回目で確定する。
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
        // **これは「建玉が戻った」ではない。** ここで幽霊行が主張を取り戻すと同じ 10 株をもう一度売ることになる。
        await f.Guard.RunOnceAsync(10);

        var total = f.Broker.MarketCloses.Sum(c => c.Intent.Quantity);
        total.Should().BeLessThanOrEqualTo(10,
            "建玉は 10 株。1 巡目 10 株、累計 20 株を売ると反対建玉（空売り）になる");
        f.Stops.Find(older.EntryDecisionId)!.RemainingProtected.Should().Be(
            0, "送信済みで未反映の決済は「戻ってきた建玉」ではない（観測が確定して帳簿が 0 になる）");
        f.Stops.Find(older.EntryDecisionId)!.State.Should().Be(
            ProtectiveStopState.Completed, "2 巡回目の観測で削りが確定し、役目を終える");
    }

    // T-10-385（受け入れ基準 39）: 幽霊行の削りが**未確定のあいだ**に、同じ銘柄・同方向で
    // **新しいエントリーが約定**する。その発注記録がまだ終端でないと行の主張は 0（`RemainingProtected` が null）なので、
    // 是正前は `excess < 0` になって **Restore が幽霊行を復活させ**、新しい建玉を
    // **その行の損切りラインとは無関係に成行決済**していた（到達していない建玉を売る）。
    // 🔴 追記(7): 復元を撤去したうえで、新規エントリーの**約定済み**株数が超過の消失を説明するため観測は確定する。
    [Fact]
    public async Task 確定前の新規エントリーの建玉で幽霊行を復活させない()
    {
        var f = NewFixture();
        var ghost = SoftwareStop(Now.AddHours(-3), quantity: 10, triggeredAt: Now);
        f.Stops.Save(ghost);
        Entry(f, ghost, OrderStatus.Filled, 10);
        f.Broker.Positions = []; // 建玉は外部で消えている

        // 巡回 1: 主張 10 株ぶんの超過を観測する（未確定なので帳簿は動かさず、完了もさせない・決済も出さない）。
        await f.Guard.RunOnceAsync(10);

        f.Broker.MarketCloses.Should().BeEmpty("建玉が無いので決済しない");
        f.Stops.Find(ghost.EntryDecisionId)!.PendingExternalReduction.Should().Be(10);
        f.Stops.Find(ghost.EntryDecisionId)!.RemainingProtected.Should().Be(10, "帳簿は確定するまで書き換えない");

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

    // ---- 7 巡目監査: 復元という操作そのものを撤去する（IADR-0344 追記(7)）----

    // T-10-410（受け入れ基準 41 / BLK-7-1）: **確定前の新規エントリーが同じ銘柄に 1 件あるだけで**、
    // 「1 巡回だけ過少に照会された行が次の巡回で戻る」が効かなくなっていた。
    // 6 巡目の是正（`AlreadyHandledShares`）が、**1 株も約定していない新規エントリーの承認数量**を
    // そのまま復元の予算から差し引いていたためである（監査の用量反応: 承認数量 0 → 主張 10・Active、
    // 承認数量 10 → 主張 0・Completed ＝**建玉 10 株が無保護**）。
    //
    // 🔴 追記(7) では**そもそも帳簿を確定まで書き換えない**ので、戻す（復元する）予算という概念が無い。
    // 超過が消えた巡回は観測の連続回数が 0 へ戻るだけであり、承認数量に依存しようがない。
    [Theory]
    [InlineData(0)]   // 対照（6 巡目のコードでも通っていた配置）
    [InlineData(10)]  // 監査が実測した配置（6 巡目のコードでは建玉が無保護になる）
    public async Task 確定前の新規エントリーがあっても一巡回だけ過少な照会で行は失われない(int newEntryQuantity)
    {
        var f = NewFixture();
        var covered = SoftwareStop(Now.AddHours(-2), quantity: 10);
        f.Stops.Save(covered);
        Entry(f, covered, OrderStatus.Filled, 10);

        if (newEntryQuantity > 0)
        {
            // 承認されたが**まだ 1 株も約定していない**新規エントリー（発注記録は未終端・約定 0 ＝ 主張も 0）。
            var fresh = SoftwareStop(Now, quantity: newEntryQuantity);
            f.Stops.Save(fresh);
            Entry(f, fresh, OrderStatus.Accepted, 0);
        }

        // 巡回 1: 建玉照会が 1 巡回だけ過少に返る（実在する 10 株が 0 に見える）。
        f.Broker.Positions = [];
        await f.Guard.RunOnceAsync(10);

        // 巡回 2: 照会が回復する。**新規エントリーの承認数量に関係なく**行も主張も失われない。
        f.Broker.Positions = [Long(10)];
        await f.Guard.RunOnceAsync(10);

        var kept = f.Stops.Find(covered.EntryDecisionId)!;
        kept.RemainingProtected.Should().Be(
            10, "建玉 10 株が実在する行の帳簿を、確定前の新規エントリーの承認数量で消してはならない");
        kept.State.Should().Be(ProtectiveStopState.Active, "建玉が実在する行を完了させると無保護になる");
        f.Broker.MarketCloses.Should().BeEmpty("未到達の行は決済しない");

        // 🔴 #820 の 8 巡目監査, IADR-0344 追記(8): 帳簿と状態が無傷でも、その行が**動かせる株数**が 0 なら保護は無い。
        // 巡回 3 で観測が失効し、到達で**実際に 10 株の決済が出る**ところまで確かめる。
        await f.Guard.RunOnceAsync(10);

        f.Stops.Find(covered.EntryDecisionId)!.EffectiveProtectedQuantity.Should().Be(10);

        await f.Executor.OnTriggeredAsync(new StopLossTriggered(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, 10, 940m, 950m, Now));

        f.Broker.MarketCloses.Sum(c => c.Intent.Quantity).Should().Be(
            10, "承認数量に依らず、建玉 10 株に損切りが出る");
    }

    // T-10-411（受け入れ基準 42 / BLK-7-2）: **S1 の記録を持たない建玉**（S2 の建玉・S0 の発注窓・人手で建てた建玉）が
    // 同じ銘柄・同方向に現れると、6 巡目の `Restore` はそれを「戻ってきた自分の建玉」と読んで幽霊行を復活させ、
    // 到達済みならその建玉を**無関係な損切りラインで成行決済**していた
    //（監査の実測: `Expected f.Broker.MarketCloses to be empty …, but found { Quantity = 10, Side = Sell, Price = 940 }`）。
    // S2 は保護記録を作らないため、**稼働中の S2 から S1 へ切り替える**この PR の反映手順で現実に起こり得る。
    //
    // 🔴 追記(7): 一度観測した超過は**書き戻さない**（確定か行の完了でしか消えない）。
    // 幽霊行の「この巡回で動かしてよい株数」は 0 のままであり、他人の建玉を売りようがない。
    [Fact]
    public async Task S1の記録を持たない建玉が現れても幽霊行は主張を取り戻さない()
    {
        var f = NewFixture();
        var ghost = SoftwareStop(Now.AddHours(-3), quantity: 10, triggeredAt: Now);
        f.Stops.Save(ghost);
        Entry(f, ghost, OrderStatus.Filled, 10);
        f.Broker.Positions = []; // 幽霊行の建玉は外部（人手決済・強制決済）で消えている

        // 巡回 1: 超過 10 株を観測する（帳簿は書かない・決済も出さない）。
        await f.Guard.RunOnceAsync(10);

        f.Broker.MarketCloses.Should().BeEmpty("建玉が無いので決済しない");

        // 🔴 **S1 の記録を一切持たない建玉**が同じ銘柄・同方向に現れる（S2 の建玉を模す）。
        // 純額は「幽霊行の建玉が戻った」ときとまったく同じに見えるが、**戻ったのではない**。
        f.Broker.Positions = [Long(10)];

        await f.Guard.RunOnceAsync(10);
        await f.Guard.RunOnceAsync(10);

        f.Broker.MarketCloses.Should().BeEmpty(
            "S1 の記録を持たない建玉を、幽霊行の損切りラインで成行決済してはならない（反対建玉になる）");
        f.Stops.Find(ghost.EntryDecisionId)!.PendingExternalReduction.Should().Be(
            10, "一度観測した超過は書き戻さない（復元という操作を持たない）");
    }

    // ---- 8 巡目監査: 1 巡回の過少照会でその行の損切りが二度と出なくなる／他人の建玉を売る ----

    // T-10-430（受け入れ基準 43 / BLK-8-1）: 追記(7) の観測値は**単調**で、確定か行の完了でしか消えなかった。
    // 建玉照会が **1 巡回だけ**過少に返っただけで `PendingExternalReduction` がその値のまま残り、
    // 以後どれだけ照会が正常でも `EffectiveProtectedQuantity` が**恒久的に 0**になる。
    // 行は `Active`・帳簿（`RemainingProtected`）も無傷なのでどの検査も通るが、到達しても 1 株も決済しない。
    // 監査の実測: 建玉 20 株・行 2 件・照会が 1 巡回だけ 10 株 → その後 10 巡回ずっと 20 株でも
    // `first: Active Remaining=10 Pending=10 Obs=0 Effective=0`、到達しても決済は 10 株だけ。
    //
    // 🔴 是正は**観測の対称な失効**である——超過が確定と同じ回数（2 巡回）連続で**消えた**ら観測を捨てる。
    // **帳簿は書き戻さない**（`RemainingProtected` は動かさない）ので、6・7 巡目の「復元」とは別物である。
    [Fact]
    public async Task 一巡回だけ過少な照会の後に照会が戻れば到達で全量が決済される()
    {
        var f = NewFixture();
        var first = SoftwareStop(Now.AddHours(-2), quantity: 10);
        var second = SoftwareStop(Now.AddHours(-1), quantity: 10);
        f.Stops.Save(first);
        f.Stops.Save(second);
        Entry(f, first, OrderStatus.Filled, 10);
        Entry(f, second, OrderStatus.Filled, 10);

        // 巡回 1: 照会が 1 巡回だけ 10 株を返す（実際の建玉は 20 株）。
        f.Broker.Positions = [Long(10)];
        await f.Guard.RunOnceAsync(10);
        f.Stops.Find(first.EntryDecisionId)!.PendingExternalReduction.Should().Be(10, "観測としては積む");

        // 巡回 2〜11: 照会は 20 株に戻り、以後ずっと正常（監査の実測と同じ 10 巡回）。
        f.Broker.Positions = [Long(20)];
        for (var cycle = 0; cycle < 10; cycle++)
            await f.Guard.RunOnceAsync(10);

        var recovered = f.Stops.Find(first.EntryDecisionId)!;
        recovered.State.Should().Be(ProtectiveStopState.Active);
        recovered.RemainingProtected.Should().Be(10, "帳簿は一度も書き換えていない");
        recovered.PendingExternalReduction.Should().Be(
            0, "超過が 2 巡回連続で消えたら観測を失効させる（確定と同じ回数で対称にする）");
        recovered.EffectiveProtectedQuantity.Should().Be(
            10, "その行が動かせる株数が恒久的に 0 のままになってはならない（損切りが二度と出ない）");

        // 到達: 建玉 20 株の**全量**に損切りが出る（是正前は 10 株しか出なかった）。
        await f.Executor.OnTriggeredAsync(new StopLossTriggered(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, 20, 940m, 950m, Now));

        f.Broker.MarketCloses.Sum(c => c.Intent.Quantity).Should().Be(
            20, "建玉 20 株のうち 10 株に損切りが出ない状態を残さない");
    }

    // T-10-431（受け入れ基準 44 / BLK-8-1 の可観測性）: 失効も確定もしないまま
    // **実効数量 0 が続く**配置（照会が超過と回復を交互に返す＝どちらの連続観測回数も溜まらない）では、
    // 行は `Active`・帳簿も無傷のまま 1 株も守らない。**到達の有無に依らず**猶予（15 分）を過ぎたら
    // Critical を **1 回だけ**出して無音をやめる（据え置き自体は続ける）。
    [Fact]
    public async Task 実効数量ゼロが猶予を過ぎたら到達していなくても一度だけ通知する()
    {
        var f = NewFixture();
        var stop = SoftwareStop(Now.AddHours(-2), quantity: 10);
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);

        var emitted = new List<SoftwareStopExecuted>();

        // 超過（建玉 0）と回復（建玉 10）が交互に見える。観測も失効も 2 巡回連続しないため、
        // 未確定の観測が消えないまま実効数量が 0 で据え置かれ続ける。
        async Task FlapAsync(int cycles)
        {
            for (var cycle = 0; cycle < cycles; cycle++)
            {
                f.Broker.Positions = f.Clock.UtcNow.Minute % 10 == 0 ? [] : [Long(10)];
                var result = await f.Guard.RunOnceAsync(10);
                emitted.AddRange(result.Events.OfType<SoftwareStopExecuted>());
                f.Clock.UtcNow = f.Clock.UtcNow.AddMinutes(5);
            }
        }

        await FlapAsync(2); // 10 分（猶予 15 分の内側）
        emitted.Where(e => (int)e.Outcome == 6).Should().BeEmpty("猶予の内側では出さない");

        await FlapAsync(2); // 20 分（猶予を越える）
        var suspended = emitted.Where(e => (int)e.Outcome == 6).Should().ContainSingle().Which;
        suspended.EntryDecisionId.Should().Be(stop.EntryDecisionId);
        suspended.Quantity.Should().Be(10, "守れていない株数を残す");
        suspended.CloseDecisionId.Should().BeNull("決済は出していない");

        await FlapAsync(4);
        emitted.Where(e => (int)e.Outcome == 6).Should().ContainSingle("毎巡回は出さない（1 行 1 回）");
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(
            ProtectiveStopState.Active, "据え置き自体は正しい fail-safe である（閉じない）");
    }

    // T-10-432（受け入れ基準 45 / BLK-8-2）: **他人の建玉が先に在る**と超過が一度も観測されず、
    // S1 の行は満額の主張を保ったまま**他人の建玉を自分の損切りラインで売る**。
    // 追記(7) の復元撤去が塞いだのは「幽霊行が先に超過を観測した後で他人の建玉が現れる」経路だけだった。
    // 監査が実測した時系列（**稼働中の S2 から S1 へ切り替えた直後そのもの**）:
    //   1. 切替前からの S2 建玉 10 株（保護記録なし） 2. 切替後の S1 エントリー 10 株が約定（純額 20・超過なし）
    //   3. S1 の建玉だけ人手で決済（純額 10・超過は依然 0） 4. 到達 → closes=[10@940/Sell]＝S2 の建玉を売った
    //
    // 🔴 純額では区別できない以上、**武装の前提条件**で塞ぐ——S1 で新規建てを武装する時点で
    // 同一銘柄・同方向に帰属不明の建玉（純額 − Active な保護記録の主張合計 > 0）があるなら、
    // 「保護レグを張れない Open では建玉を持たない」（IADR-0210 決定1）に合わせて**建玉を持たずに見送る**。
    [Fact]
    public async Task 帰属不明の建玉が先にある銘柄ではS1を武装せず幽霊行も生じない()
    {
        var f = NewFixture();

        // 1. 切替前からの S2 の建玉 10 株（保護記録を作らない実行機構なので、主張する行が無い）。
        f.Broker.Positions = [Long(10)];

        // 2. 切替後の S1 の新規建てが承認されて届く。
        var approved = Approved();

        var result = await f.Execution.ExecuteAsync(approved);

        ((int)result.Forgone!.Reason).Should().Be(
            4, "OrderDispatchForgoneReason.UnattributedPosition（末尾へ追加した序数）");
        f.Broker.Entries.Should().BeEmpty("帰属不明の建玉があるあいだは新規建てを送らない（建玉を持たない側へ倒す）");
        f.Stops.Find(approved.DecisionId).Should().BeNull("幽霊行の元になる保護記録を作らない");
        result.SoftwareStopArmed.Should().BeNull();

        // 3. S1 の建玉は生じていないので、人手の決済があっても純額は S2 のぶん（10 株）のままである。
        // 4. 到達しても、S2 の建玉を S1 の損切りラインで売る行が存在しない。
        var run = await f.Executor.OnTriggeredAsync(new StopLossTriggered(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, 10, 940m, 950m, Now));

        run.Matched.Should().Be(0);
        f.Broker.MarketCloses.Should().BeEmpty(
            "他の実行機構（S2・人手・S0 の発注窓）の建玉を S1 の損切りラインで売ってはならない");
    }

    // T-10-433（受け入れ基準 45 の対の肯定形）: 帰属不明の建玉が**無い**銘柄では従来どおり武装する
    // （前提条件が「S1 をいつまでも武装できない」ゲートに化けていないことを固定する）。
    // 自分の S1 行が既に主張している建玉は帰属不明ではない。
    [Fact]
    public async Task 帰属が付いている建玉しかない銘柄では従来どおりS1を武装する()
    {
        var f = NewFixture();
        var existing = SoftwareStop(Now.AddHours(-2), quantity: 10);
        f.Stops.Save(existing with { RemainingProtected = 10 }); // 既存行の主張は確定済み
        Entry(f, existing, OrderStatus.Filled, 10);
        f.Broker.Positions = [Long(10)];                          // その 10 株だけが在る

        var approved = Approved();

        var result = await f.Execution.ExecuteAsync(approved);

        result.Forgone.Should().BeNull("帰属不明の建玉は無い");
        f.Broker.Entries.Should().ContainSingle();
        f.Stops.Find(approved.DecisionId)!.Mechanism.Should().Be(StopLossExecutionMethod.SoftwareStop);
        result.SoftwareStopArmed.Should().NotBeNull();
    }

    private static OrderApproved Approved() =>
        new(Guid.NewGuid(),
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
                BrokerProvider.MoomooSimulate, 10, 1_000m, PositionEffect.Open, StopLossPrice: 950m, FxRateToBase: 1m),
            10, Now, StopLossMethod: StopLossExecutionMethod.SoftwareStop);
}
