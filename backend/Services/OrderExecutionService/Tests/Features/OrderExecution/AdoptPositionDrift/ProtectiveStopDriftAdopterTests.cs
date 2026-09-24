using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.AdoptPositionDrift;
using OrderExecutionService.Features.OrderExecution.AmendOrder;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-641〜T-10-645・T-10-734〜T-10-738, FR-10, FR-05, FR-11, UC-02, UC-06, #858, IADR-0370, IADR-0350 決定5:
// **利用者が承認した乖離の取り込みに、発注執行側の保護記録とブローカー側の保護注文を追随させる。**
//
// 取り込み（#849）は取引台帳だけを観測値へ合わせるため、保護記録は何も知らされないまま残っていた。
// 🔴 **建玉が無いのに売りの逆指値がブローカーに残ると、発火したとき意図しないショートが建つ**（#853 と同じ帰結）。
//
// 本クラスは「消えた建玉の保護を取り消して終端化する」「取り消せたと確認できなければ黙って閉じない」
// 「無関係な銘柄に触らない」「再送で二重に取り消さない」「保護を消しすぎない」を固定する。
// 🔴 PR #918 の監査（IADR-0370 2026-09-24 追記）: 「建玉照会が不明・失敗なら帳簿もブローカーも変えない」を加える。
// 🔴 T-10-780〜T-10-782, NFR-07, #942, IADR-0395: 最後の配送の打ち切りだけを業務メトリクスへ理由つきで数える（末尾の節）。
public class ProtectiveStopDriftAdopterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 7, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // 取消の送信後に照会が返す状態を注入できるブローカー（moomoo の実挙動を模す。OrderCancellationConfirmationTests と同型）。
    private sealed class ScriptedBroker(OrderStatus? afterCancel = OrderStatus.Cancelled, bool cancelThrows = false)
        : IBrokerAdapter, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public int CancelCount { get; private set; }

        public List<string> CancelledOrderIds { get; } = [];

        /// <summary>建玉照会の応答（null＝照会不能＝不明）。</summary>
        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = [];

        /// <summary>建玉照会が例外で落ちる（OpenD の応答異常など）。</summary>
        public bool PositionsThrow { get; set; }

        /// <summary>取消を 1 本送った時点で呼ばれる（停止要求の注入に使う）。</summary>
        public Action? OnCancelSent { get; set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException("本テストは発注しない");

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(afterCancel is { } status
                ? new BrokerOrder(orderId, CloseIntent(), status, 0, 0m, Now, Now)
                : null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            CancelCount++;
            CancelledOrderIds.Add(orderId);
            OnCancelSent?.Invoke();
            return cancelThrows ? throw new InvalidOperationException("取消に失敗（テスト）") : Task.CompletedTask;
        }

        /// <summary>建玉照会が呼ばれた回数。</summary>
        public int PositionQueryCount { get; private set; }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default)
        {
            PositionQueryCount++;
            return PositionsThrow
                ? throw new InvalidOperationException("建玉照会に失敗（テスト）")
                : Task.FromResult(Positions);
        }
    }

    private static OrderIntent CloseIntent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
            10, 950m, PositionEffect.Close);

    private sealed record Harness(
        ProtectiveStopDriftAdopter Adopter,
        ScriptedBroker Broker,
        InMemoryProtectiveStopOrderStore Stops,
        InMemoryExecutedOrderStore Store);

    private static Harness NewHarness(
        ScriptedBroker? broker = null, bool withPositionSource = true,
        SoftwareStopLivenessReporterTests.RecordingLogger<ProtectiveStopDriftAdopter>? logger = null,
        BusinessMetrics? metrics = null)
    {
        broker ??= new ScriptedBroker();
        var stops = new InMemoryProtectiveStopOrderStore();
        var store = new InMemoryExecutedOrderStore();
        var amendments = new OrderAmendmentService(
            broker, store, new InMemoryOrderLifecycleStore(), new FakeClock());
        var adopter = new ProtectiveStopDriftAdopter(
            stops, amendments, new FakeClock(), logger, positions: withPositionSource ? broker : null, metrics);
        return new Harness(adopter, broker, stops, store);
    }

    /// <summary>ブローカー側逆指値（S0）の行と、その発注記録（取消は DecisionId から注文 ID を引く）。</summary>
    private static ProtectiveStopOrder AddBrokerStop(
        Harness h, string symbol = "AAPL", Market market = Market.UnitedStates,
        TradeSide entrySide = TradeSide.Buy, int quantity = 10, string orderId = "stop-1")
    {
        var entryDecisionId = Guid.NewGuid();
        var stopDecisionId = ProtectiveStopIds.StopDecisionId(entryDecisionId, attempt: 1);
        var stop = new ProtectiveStopOrder(
            entryDecisionId, stopDecisionId, orderId, symbol, market, entrySide, ProductType.Cash,
            BrokerProvider.MoomooSimulate, quantity, 950m, 1m, Attempt: 1, ProtectiveStopState.Active,
            Now.AddMinutes(-30), Now.AddMinutes(-30), RemainingProtected: quantity);
        h.Stops.Save(stop);
        h.Store.Save(new ExecutionRecord(
            stopDecisionId, orderId, symbol, market, entrySide == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy,
            ProductType.Cash, PositionEffect.Close, quantity, 950m, 0, 0m, OrderStatus.Accepted, 0m, Now.AddMinutes(-30)));
        return stop;
    }

    /// <summary>ソフトウェア逆指値（S1）の行（ブローカーに注文が無い＝帳簿だけの行）。</summary>
    private static ProtectiveStopOrder AddSoftwareStop(Harness h, int quantity = 4, DateTimeOffset? createdAt = null)
    {
        var created = createdAt ?? Now.AddMinutes(-20);
        var entryDecisionId = Guid.NewGuid();
        var stop = new ProtectiveStopOrder(
            entryDecisionId, Guid.NewGuid(), string.Empty, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, 950m, 1m, Attempt: 0,
            ProtectiveStopState.Active, created, created,
            Mechanism: StopLossExecutionMethod.SoftwareStop, RemainingProtected: quantity);
        h.Stops.Save(stop);
        return stop;
    }

    private static PositionDriftAdopted Adopted(
        int before = 10, int after = 0, string symbol = "AAPL", Market market = Market.UnitedStates) =>
        new(Guid.NewGuid(), symbol, market, before, after, after, Now.AddMinutes(-5), 1_000m,
            RealizedPnlRecorded: false, ReferencePrice: null, EstimatedPnlInBase: null,
            Actor: "owner", Reason: "証券会社のアプリで全株売却", AdoptedAt: Now);

    // ---- T-10-641: 消えた建玉の保護レグを取り消して終端化する ----

    [Fact]
    public async Task 取り込みで建玉が消えたら_保護レグを取り消して記録を終端化し監査に残す()
    {
        var h = NewHarness();
        var stop = AddBrokerStop(h);

        var result = await h.Adopter.ApplyAsync(Adopted());

        h.Broker.CancelCount.Should().Be(1, "建玉が無いのに残る逆指値は、発火すると意図しないショートを建てる");
        h.Broker.CancelledOrderIds.Should().Equal("stop-1");
        result.Reduced.Should().Be(1);
        result.CancelUnconfirmed.Should().Be(0);

        var current = h.Stops.Find(stop.EntryDecisionId)!;
        current.State.Should().Be(ProtectiveStopState.Completed);
        current.RemainingProtected.Should().Be(0);

        // 取り消せたと**確認できた**ので在庫の押さえを解く（保護レグの承認行は台帳に残っている）。
        var cancelled = result.Events.OfType<OrderCancelled>().Should().ContainSingle().Which;
        cancelled.DecisionId.Should().Be(stop.StopDecisionId);
        cancelled.Reason.Should().Contain("乖離の取り込み").And.Contain("owner");

        // 保護が減ったことは監査・通知へ出す（無音の不可逆動作を残さない）。
        var reduced = result.Events.OfType<SoftwareStopExecuted>().Should().ContainSingle().Which;
        reduced.Outcome.Should().Be(SoftwareStopOutcome.ProtectionReduced);
        reduced.Quantity.Should().Be(10);
    }

    // ---- T-10-642: 取り消せたと確認できなければ黙って閉じない ----

    [Theory]
    [InlineData(null, false)]                      // 取消は送れたが照会が不明（null）
    [InlineData(OrderStatus.Accepted, false)]      // まだ終端でない（取消進行中に約定し得る）
    [InlineData(OrderStatus.Cancelled, true)]      // 取消の送信そのものが失敗した
    public async Task 保護レグを取り消せたと確認できなければ_記録はActiveのままでCriticalを出す_否定形(
        OrderStatus? afterCancel, bool cancelThrows)
    {
        var h = NewHarness(new ScriptedBroker(afterCancel, cancelThrows));
        var stop = AddBrokerStop(h);

        var result = await h.Adopter.ApplyAsync(Adopted());

        result.CancelUnconfirmed.Should().Be(1);
        result.Reduced.Should().Be(0);
        h.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active,
            "取り消せたと確認できていない逆指値の記録を閉じると、生きた注文が誰の巡回からも外れる");
        result.Events.OfType<OrderCancelled>().Should().BeEmpty("確認できないまま在庫の押さえを解かない");

        var alert = result.Events.OfType<SoftwareStopExecuted>().Should().ContainSingle().Which;
        alert.Outcome.Should().Be(SoftwareStopOutcome.StopCancelUnconfirmed);
        alert.Quantity.Should().Be(10);
        alert.CloseOrderId.Should().Be("stop-1");
    }

    // ---- T-10-643: 無関係な銘柄・方向に触らない ----

    [Fact]
    public async Task 取り込みと無関係な銘柄や方向の保護レグには触らない_否定形()
    {
        var h = NewHarness();
        var other = AddBrokerStop(h, symbol: "MSFT", orderId: "stop-msft");
        var japan = AddBrokerStop(h, symbol: "AAPL", market: Market.Japan, orderId: "stop-jp");
        var shortSide = AddBrokerStop(h, entrySide: TradeSide.Sell, orderId: "stop-short");
        var target = AddBrokerStop(h, orderId: "stop-target");

        var result = await h.Adopter.ApplyAsync(Adopted());

        h.Broker.CancelledOrderIds.Should().Equal("stop-target");
        result.Reduced.Should().Be(1);
        foreach (var untouched in new[] { other, japan, shortSide })
        {
            h.Stops.Find(untouched.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
            h.Stops.Find(untouched.EntryDecisionId)!.RemainingProtected.Should().Be(10);
        }
    }

    // ---- T-10-644: 再送で二重に取り消さない ----

    [Fact]
    public async Task 同じ取り込みを再送しても二度取り消さない_否定形()
    {
        var h = NewHarness();
        AddBrokerStop(h);
        var adopted = Adopted();

        var first = await h.Adopter.ApplyAsync(adopted);
        var second = await h.Adopter.ApplyAsync(adopted);

        h.Broker.CancelCount.Should().Be(1, "目標は絶対値（取り込み後の数量）であって差分ではない");
        first.Reduced.Should().Be(1);
        second.Reduced.Should().Be(0);
        second.Events.Should().BeEmpty();
    }

    // ---- T-10-645: 部分的な取り込み／保護を消しすぎない ----

    [Fact]
    public async Task 部分的な取り込みは帳簿だけの行から減らし_生きている逆指値は取り消さない()
    {
        var h = NewHarness();
        var brokerStop = AddBrokerStop(h, quantity: 10, orderId: "stop-live");
        var softwareStop = AddSoftwareStop(h, quantity: 4);
        h.Broker.Positions = [new("AAPL", Market.UnitedStates, 11, 1_000m)];

        // 台帳 14 株 → 11 株（3 株がシステム外で売られた）。
        var result = await h.Adopter.ApplyAsync(Adopted(before: 14, after: 11));

        h.Broker.CancelCount.Should().Be(0, "生きた逆指値を取り消して張り直す取引はしない（無保護の窓を作る）");
        h.Stops.Find(brokerStop.EntryDecisionId)!.RemainingProtected.Should().Be(10, "実注文を持つ行は全部か 0 か");
        h.Stops.Find(softwareStop.EntryDecisionId)!.RemainingProtected.Should().Be(1, "帳簿だけの行が先に吸収する");
        result.Reduced.Should().Be(1);
        result.Events.OfType<SoftwareStopExecuted>().Should().ContainSingle()
            .Which.Outcome.Should().Be(SoftwareStopOutcome.ProtectionReduced);
    }

    [Fact]
    public async Task 新しい建玉照会が建玉の存在を示すなら保護を消さない_否定形()
    {
        // 取り込みの観測は最大 60 分古い。その間に利用者が買い戻していれば、消すのは**実在する建玉の保護**である。
        var h = NewHarness();
        var stop = AddBrokerStop(h);
        h.Broker.Positions = [new("AAPL", Market.UnitedStates, 10, 1_000m)];

        var result = await h.Adopter.ApplyAsync(Adopted());

        h.Broker.CancelCount.Should().Be(0);
        h.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
        result.Reduced.Should().Be(0);
        result.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task 建玉を照会できない構成では取り込みの観測に従う()
    {
        // 建玉照会を持たない発注先（内蔵 paper）。何もしないと孤立した逆指値が残る側なので、
        // 利用者が承認した取り込みの観測に従う。
        var h = NewHarness(withPositionSource: false);
        var stop = AddBrokerStop(h);

        var result = await h.Adopter.ApplyAsync(Adopted());

        h.Broker.CancelCount.Should().Be(1);
        h.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        result.Reduced.Should().Be(1);
    }

    // ---- T-10-734: 建玉照会が例外で落ちたら、帳簿もブローカーも変えない（PR #918 監査で反転） ----
    // 🔴 旧版はここで「取り込みの観測（最大 60 分前）に従って取り消す」を固定していた。
    // 建玉が消えたと**確かめられない**まま保護を消すと、その間に買い戻した実在の建玉が無保護になる。
    // 照会ができる構成で照会が落ちたのは「照会できない構成」とは違う —— 再試行で照会をやり直す。
    [Fact]
    public async Task 建玉照会が例外で落ちたら帳簿もブローカーも変えずCriticalを出して再試行へ回す_否定形()
    {
        var logger = new SoftwareStopLivenessReporterTests.RecordingLogger<ProtectiveStopDriftAdopter>();
        var h = NewHarness(logger: logger);
        h.Broker.PositionsThrow = true;
        var stop = AddBrokerStop(h);

        var thrown = await Assert.ThrowsAsync<ProtectiveStopDriftPositionsUnknownException>(
            () => h.Adopter.ApplyAsync(Adopted()));

        h.Broker.CancelCount.Should().Be(0, "建玉が消えたと確かめられないまま保護を取り消さない");
        var current = h.Stops.Find(stop.EntryDecisionId)!;
        current.State.Should().Be(ProtectiveStopState.Active);
        current.RemainingProtected.Should().Be(10);
        thrown.InnerException.Should().BeOfType<InvalidOperationException>("照会の失敗の原因を運ぶ");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Critical)
            .Which.Message.Should().Contain("変えません").And.Contain("AAPL");
    }

    // ---- T-10-735: 建玉照会が不明（null）でも同じ（0 株と読まない） ----
    [Fact]
    public async Task 建玉照会が不明なら0株と読まず帳簿もブローカーも変えない_否定形()
    {
        var logger = new SoftwareStopLivenessReporterTests.RecordingLogger<ProtectiveStopDriftAdopter>();
        var h = NewHarness(logger: logger);
        h.Broker.Positions = null;
        var brokerStop = AddBrokerStop(h, quantity: 10);
        var softwareStop = AddSoftwareStop(h, quantity: 4);

        await Assert.ThrowsAsync<ProtectiveStopDriftPositionsUnknownException>(
            () => h.Adopter.ApplyAsync(Adopted(before: 14, after: 0)));

        h.Broker.CancelCount.Should().Be(0, "不明は「建玉が無い」ではない");
        h.Stops.Find(brokerStop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
        h.Stops.Find(brokerStop.EntryDecisionId)!.RemainingProtected.Should().Be(10);
        h.Stops.Find(softwareStop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
        h.Stops.Find(softwareStop.EntryDecisionId)!.RemainingProtected.Should().Be(4, "帳簿だけの行も削らない");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Critical);
    }

    // ---- T-10-736: 減らすものが無いなら照会せず、照会の失敗で再試行を作らない ----
    [Fact]
    public async Task 主張が取り込みの目標以下なら照会せず_照会が落ちていても投げない()
    {
        var h = NewHarness();
        h.Broker.PositionsThrow = true;
        var stop = AddSoftwareStop(h, quantity: 4);

        // 台帳 10 株 → 6 株。主張は 4 株しかない（目標 6 株以下）＝照会の答えに依らず減らすものは無い。
        var result = await h.Adopter.ApplyAsync(Adopted(before: 10, after: 6));

        h.Broker.PositionQueryCount.Should().Be(0);
        result.Reduced.Should().Be(0);
        result.Events.Should().BeEmpty();
        h.Stops.Find(stop.EntryDecisionId)!.RemainingProtected.Should().Be(4);
    }

    // 🔴 停止要求は**行の処理を終えてから**見る。1 行目の取消を送った直後に停止されても、
    // その行の結果（取消・保護減少）は返って発行される（無音にしない）。残りの行は Active のまま。
    [Fact]
    public async Task 停止要求で打ち切っても処理済みの行の結果は握り潰さない_否定形()
    {
        var h = NewHarness();
        using var cts = new CancellationTokenSource();
        h.Broker.OnCancelSent = cts.Cancel;
        var first = AddBrokerStop(h, quantity: 10, orderId: "stop-a");
        var second = AddBrokerStop(h, quantity: 10, orderId: "stop-b");

        var result = await h.Adopter.ApplyAsync(Adopted(before: 20, after: 0), cts.Token);

        h.Broker.CancelCount.Should().Be(1, "停止要求の後は次の行へ進まない");
        result.Reduced.Should().Be(1);
        result.Events.OfType<OrderCancelled>().Should().ContainSingle("送った取消の結果を握り潰さない");
        result.Events.OfType<SoftwareStopExecuted>().Should().ContainSingle()
            .Which.Outcome.Should().Be(SoftwareStopOutcome.ProtectionReduced);

        var processed = h.Stops.Find(first.EntryDecisionId)!.State == ProtectiveStopState.Completed ? first : second;
        var untouched = processed == first ? second : first;
        h.Stops.Find(processed.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        h.Stops.Find(untouched.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active,
            "打ち切った残りは Active のまま＝次の観測・巡回・取り込みが引き続き見る");
    }

    [Fact]
    public async Task 増える方向や方向の反転の取り込みでは保護を削らない_否定形()
    {
        var h = NewHarness();
        var stop = AddBrokerStop(h);

        var increased = await h.Adopter.ApplyAsync(Adopted(before: 10, after: 12));
        var reversed = await h.Adopter.ApplyAsync(Adopted(before: 10, after: -3));

        increased.Events.Should().BeEmpty();
        reversed.Events.Should().BeEmpty();
        h.Broker.CancelCount.Should().Be(0);
        h.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }

    // ---- T-10-737: 方向の検査は帳簿だけの行（S1）で効いていることを固定する ----
    // 上の S0 だけの試験は「全部か 0 か」が結果を覆い隠す（反転 10→−3 の目標 3 株では 10 株の S0 に届かない）。
    // S1 は部分的に削れるので、検査が無ければ実際に削られる。主張 15 株は台帳 10 株を上回る（古い主張が残った状態）。
    [Theory]
    [InlineData(10, 12)]   // 増える方向
    [InlineData(10, -3)]   // 方向の反転
    [InlineData(-10, 3)]   // 方向の反転（売り建て側から）
    public async Task 増加や反転の取り込みでは帳簿だけの行も削らない_否定形(int before, int after)
    {
        var h = NewHarness();
        var stop = AddSoftwareStop(h, quantity: 15);
        if (before < 0)
        {
            stop = stop with { EntrySide = TradeSide.Sell };
            h.Stops.Save(stop);
        }

        var result = await h.Adopter.ApplyAsync(Adopted(before, after));

        result.Events.Should().BeEmpty();
        result.Reduced.Should().Be(0);
        h.Stops.Find(stop.EntryDecisionId)!.RemainingProtected.Should().Be(15);
        h.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }

    // ---- T-10-738: 同じ銘柄の S1 が 2 行（稼働環境の AAPL 715 株・713 株）。古い行から使い切る ----
    [Fact]
    public async Task 同じ銘柄の帳簿だけの行が2行なら部分的な取り込みは古い行から使い切る()
    {
        var h = NewHarness();
        var older = AddSoftwareStop(h, quantity: 715, createdAt: Now.AddHours(-3));
        var newer = AddSoftwareStop(h, quantity: 713, createdAt: Now.AddHours(-1));
        h.Broker.Positions = [new("AAPL", Market.UnitedStates, 500, 1_000m)];

        // 台帳 1,428 株 → 500 株（928 株がシステム外で売られた）。
        var result = await h.Adopter.ApplyAsync(Adopted(before: 1_428, after: 500));

        var olderNow = h.Stops.Find(older.EntryDecisionId)!;
        var newerNow = h.Stops.Find(newer.EntryDecisionId)!;
        olderNow.RemainingProtected.Should().Be(0, "作成の古い行から使い切る（巡回と同じ規則）");
        olderNow.State.Should().Be(ProtectiveStopState.Completed);
        newerNow.RemainingProtected.Should().Be(500, "残りの 213 株だけを新しい行から減らす");
        newerNow.State.Should().Be(ProtectiveStopState.Active);
        h.Broker.CancelCount.Should().Be(0);
        result.Reduced.Should().Be(2);
        result.Events.OfType<SoftwareStopExecuted>().Select(e => (e.EntryDecisionId, e.Quantity))
            .Should().Equal((older.EntryDecisionId, 715), (newer.EntryDecisionId, 213));
    }

    // ==== #942, IADR-0395: 再試行を使い切った打ち切りを業務メトリクスへ数える（アラートが引く系列） ====
    // 🔴 Meter はプロセス全体で観測されるため、本節はすべて**テストごとに一意な Meter 名**で組む（否定形を含むため。#695）。

    private static (MeterCapture Capture, BusinessMetrics Metrics) IsolatedMetrics(
        [System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
    {
        var name = MeterCapture.NewIsolatedMeterName(caller);
        return (new MeterCapture(name), BusinessMetrics.WithMeterName(name));
    }

    // ---- T-10-780: 最後の配送で照会が不明（null）→ reason=positions-unknown を 1 件。何も変えずに投げる ----
    [Fact]
    public async Task 最後の配送で建玉照会が不明なら打ち切りを不明の理由で1件数えて投げる()
    {
        var (capture, metrics) = IsolatedMetrics();
        using var _c = capture;
        using var _m = metrics;
        var logger = new SoftwareStopLivenessReporterTests.RecordingLogger<ProtectiveStopDriftAdopter>();
        var h = NewHarness(logger: logger, metrics: metrics);
        h.Broker.Positions = null;
        var stop = AddBrokerStop(h);

        await Assert.ThrowsAsync<ProtectiveStopDriftPositionsUnknownException>(
            () => h.Adopter.ApplyAsync(Adopted(), finalDeliveryAttempt: true));

        capture.ValuesOf(BusinessMetricNames.DriftAdoptionFollowUpAbandoned).Should().ContainSingle()
            .Which.Should().Match<MeterCapture.Measurement>(m =>
                m.Value == 1 && m.Tags[BusinessMetricNames.TagReason] == BusinessMetrics.DriftFollowUpPositionsUnknown);
        h.Broker.CancelCount.Should().Be(0, "数えても保護は変えない（建玉が消えたと確かめられていない）");
        h.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Critical)
            .Which.Message.Should().Contain("_error キューへ送られます", "最後の配送だと運用者がログからも読める");
    }

    // ---- T-10-781: 最後の配送で照会が例外 → reason=positions-query-failed（不明と失敗を混ぜない） ----
    [Fact]
    public async Task 最後の配送で建玉照会が例外なら打ち切りを照会失敗の理由で1件数える()
    {
        var (capture, metrics) = IsolatedMetrics();
        using var _c = capture;
        using var _m = metrics;
        var h = NewHarness(metrics: metrics);
        h.Broker.PositionsThrow = true;
        AddBrokerStop(h);

        await Assert.ThrowsAsync<ProtectiveStopDriftPositionsUnknownException>(
            () => h.Adopter.ApplyAsync(Adopted(), finalDeliveryAttempt: true));

        capture.TagValuesOf(BusinessMetricNames.DriftAdoptionFollowUpAbandoned, BusinessMetricNames.TagReason)
            .Should().Equal(BusinessMetrics.DriftFollowUpPositionsQueryFailed);
        capture.SumOf(BusinessMetricNames.DriftAdoptionFollowUpAbandoned).Should().Be(1);
    }

    // ---- T-10-782: 数えない場合（否定形）。途中の配送・空の一覧（0 株）・建玉あり・照会を持たない構成 ----
    // 🔴 途中の配送を数えると、再試行で回復する一過性の照会失敗 1 回でアラートが鳴る。
    // 🔴 空の一覧は「不明」ではなく「確かめた・建玉なし」であり、追随は進む（不明・なし・ありを混ぜない）。
    [Fact]
    public async Task 最後でない配送の打ち切りは数えない_否定形()
    {
        var (capture, metrics) = IsolatedMetrics();
        using var _c = capture;
        using var _m = metrics;
        var h = NewHarness(metrics: metrics);
        h.Broker.Positions = null;
        AddBrokerStop(h);

        await Assert.ThrowsAsync<ProtectiveStopDriftPositionsUnknownException>(
            () => h.Adopter.ApplyAsync(Adopted(), finalDeliveryAttempt: false));
        h.Broker.PositionsThrow = true;
        await Assert.ThrowsAsync<ProtectiveStopDriftPositionsUnknownException>(
            () => h.Adopter.ApplyAsync(Adopted(), finalDeliveryAttempt: false));

        capture.ValuesOf(BusinessMetricNames.DriftAdoptionFollowUpAbandoned).Should().BeEmpty();
    }

    [Theory]
    [InlineData("none")] // 照会は成功・0 株（確かめた）→ 追随して取り消す
    [InlineData("present")] // 照会は成功・建玉あり → 保護を消さない
    [InlineData("no-source")] // 建玉照会を持たない構成（内蔵 paper）→ 取り込みの観測に従う
    public async Task 最後の配送でも打ち切らなければ数えない_否定形(string positions)
    {
        var (capture, metrics) = IsolatedMetrics();
        using var _c = capture;
        using var _m = metrics;
        var h = NewHarness(withPositionSource: positions != "no-source", metrics: metrics);
        h.Broker.Positions = positions == "present"
            ? [new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 10, 1_000m)]
            : [];
        AddBrokerStop(h);

        var result = await h.Adopter.ApplyAsync(Adopted(), finalDeliveryAttempt: true);

        capture.ValuesOf(BusinessMetricNames.DriftAdoptionFollowUpAbandoned).Should().BeEmpty();
        h.Broker.CancelCount.Should().Be(positions == "present" ? 0 : 1, "前提: 打ち切らずに追随が進んだ");
        result.Scanned.Should().Be(1);
    }
}
