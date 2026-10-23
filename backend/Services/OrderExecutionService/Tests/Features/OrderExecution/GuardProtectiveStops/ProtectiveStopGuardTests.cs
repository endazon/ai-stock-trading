using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-10, UC-02, #331, IADR-0210 決定4: 保護逆指値ガード（失効検知・再発注・残存取消）の検証。
// 業務フロー 02「逆指値の未受理・失効を検知 → 再発注、不可なら成行で手仕舞い」の分岐表と、
// 「建玉なき逆指値を残さない」（反対建玉の防止）・「不明は据え置く」（fail-safe）を固定する。
public class ProtectiveStopGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 7, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class GuardBroker : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource, IClassifiedPositionSource
    {
        /// <summary>
        /// FR-10, #1093, IADR-0458: 分類つきの照会が返す結果の列（先頭から順に使う）。空なら <see cref="Positions"/> から作る
        /// （null は分類できない失敗）。照会し直しを渡さないガードはこの口を使わない（従来の GetPositionsAsync だけ）。
        /// </summary>
        public Queue<PositionQueryResult> ClassifiedResults { get; } = new();
        public int ClassifiedQueryCount { get; private set; }
        public int PositionQueryCount { get; private set; }

        /// <summary>NFR, #1092: 建玉照会（分類つき・従来のどちらも）が投げる例外を作る（null なら投げない）。</summary>
        public Func<Exception>? ThrowOnPositionQuery { get; set; }

        public Task<PositionQueryResult> QueryPositionsAsync(CancellationToken ct = default)
        {
            ClassifiedQueryCount++;
            if (ThrowOnPositionQuery is not null)
                throw ThrowOnPositionQuery();
            return Task.FromResult(ClassifiedResults.Count > 0
                ? ClassifiedResults.Dequeue()
                : Positions is null
                    ? PositionQueryResult.Failed(PositionQueryFailure.Other)
                    : PositionQueryResult.Success(Positions));
        }

        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        /// <summary>StopOrderId → 照会結果（未登録は null＝照会不能）。</summary>
        public Dictionary<string, BrokerOrder?> Orders { get; } = new();

        /// <summary>建玉スナップショット（null＝照会不能）。</summary>
        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = [];

        public bool RejectReplacement { get; set; }
        public bool ThrowOnMarketClose { get; set; }

        /// <summary>この StopOrderId の注文照会は例外になる（ブローカ側の異常。その 1 件の評価が落ちる）。</summary>
        public HashSet<string> ThrowOnGetOrderIds { get; } = [];

        /// <summary>
        /// 再発注の送信が例外になる（接続断＝接続確立の失敗。確実に未発注）。
        /// #853, IADR-0428 決定1: 届いたか不明・分類できない例外は成行へ倒れず据え置く（ProtectiveStopGuardIndeterminateStopTests が固定する）。
        /// </summary>
        public bool ThrowOnStopPlace { get; set; }

        public List<string> Cancelled { get; } = [];
        public int StopPlaceCount { get; private set; }
        public int MarketCloseCount { get; private set; }

        /// <summary>#1449: 逆指値の張り直しを受け付けた直後に呼ばれる（並行更新の再現に使う）。</summary>
        public Action? AfterStopPlace { get; set; }

        /// <summary>#1446: 成行を送る直前に呼ばれる（直列化を握っているかを確かめる）。</summary>
        public Action? BeforeMarketClose { get; set; }
        public OrderIntent? LastStopIntent { get; private set; }
        public OrderIntent? LastCloseIntent { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException("ガードは通常発注を行わない");

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default)
        {
            StopPlaceCount++;
            LastStopIntent = closeIntent;
            if (ThrowOnStopPlace)
                throw new BrokerUnavailableException("OpenD 切断・再発注は未発注（テスト）");
            AfterStopPlace?.Invoke();
            return Task.FromResult(new BrokerOrder(
                $"stop-re-{StopPlaceCount}", closeIntent,
                RejectReplacement ? OrderStatus.Rejected : OrderStatus.Accepted, 0, 0m, Now,
                RejectReplacement ? Now : null));
        }

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            BeforeMarketClose?.Invoke();
            MarketCloseCount++;
            LastCloseIntent = closeIntent;
            // #848, IADR-0117（改定 7）: 「次の巡回で再試行できる失敗」は**確実に未発注**（接続確立の失敗）だけである。
            // 分類できない例外・届いたか不明は逆向き（撃ち直さない）であり、T-10-409 が固定する。
            return ThrowOnMarketClose
                ? throw new BrokerUnavailableException("OpenD 切断・成行手仕舞いは未発注（テスト）")
                : Task.FromResult(new BrokerOrder(
                    $"close-{MarketCloseCount}", closeIntent, OrderStatus.Filled,
                    closeIntent.Quantity, closeIntent.Price, Now, Now));
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            ThrowOnGetOrderIds.Contains(orderId)
                ? throw new InvalidOperationException($"注文照会に失敗（テスト）: {orderId}")
                : Task.FromResult(Orders.TryGetValue(orderId, out var order) ? order : null);

        /// <summary>#1449, IADR-0576 決定2: 取消を受け付けても照会では生きたまま（取消待ち・届いていない）に見せる。</summary>
        public bool CancelLeavesWorking { get; set; }

        /// <summary>#1449, IADR-0576 決定2: 取消の窓で約定させる（取消は「約定済み」の InvalidOperationException で失敗する）。</summary>
        public bool FillsBeforeCancel { get; set; }

        /// <summary>#1449: 取消の直後（照会の前）に呼ばれる（並行更新の再現に使う）。</summary>
        public Action? AfterCancel { get; set; }

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            Cancelled.Add(orderId);
            if (Orders.TryGetValue(orderId, out var working) && working is { Status: OrderStatus.Accepted or OrderStatus.PartiallyFilled })
            {
                if (FillsBeforeCancel)
                {
                    Orders[orderId] = working with
                    {
                        Status = OrderStatus.Filled,
                        FilledQuantity = working.Intent.Quantity,
                        AveragePrice = working.Intent.Price,
                        CompletedAt = Now,
                    };
                    throw new InvalidOperationException($"約定済みの注文は取り消せません（テスト）: {orderId}");
                }

                // 本物の証券会社と同じく、取消した注文は照会で終端（Cancelled）に見える。
                if (!CancelLeavesWorking)
                    Orders[orderId] = working with { Status = OrderStatus.Cancelled, CompletedAt = Now };
            }

            AfterCancel?.Invoke();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default)
        {
            PositionQueryCount++;
            if (ThrowOnPositionQuery is not null)
                throw ThrowOnPositionQuery();
            return Task.FromResult(Positions);
        }
    }

    private static ProtectiveStopOrder ActiveStop(
        TradeSide entrySide = TradeSide.Buy, int quantity = 10, int attempt = 1, string stopOrderId = "stop-1") =>
        new(Guid.NewGuid(), Guid.NewGuid(), stopOrderId, "AAPL", Market.UnitedStates, entrySide,
            ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, 950m, 1m, attempt,
            ProtectiveStopState.Active, Now.AddMinutes(-5), Now.AddMinutes(-5));

    private static BrokerOrder StopOrder(OrderStatus status) =>
        new("stop-1", new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 10, 950m, PositionEffect.Close), status, 0, 0m, Now,
            OrderStatusLifecycle.IsTerminal(status) ? Now : null);

    private static (ProtectiveStopGuard Guard, GuardBroker Broker, InMemoryProtectiveStopOrderStore Stops,
        InMemoryExecutedOrderStore Store) NewGuard(
            ProtectiveStopOrder stop, IClosePositionLock? closeLock = null, ContendedStopStore? contended = null)
    {
        var broker = new GuardBroker();
        var stops = new InMemoryProtectiveStopOrderStore();
        stops.Save(stop);
        var store = new InMemoryExecutedOrderStore();
        // #1013, IADR-0428（2026-09-26 追記）: エントリーは約定済み（発注記録が終端）。この表は「建って消えた」側の分岐を固定する。
        // 未約定・不明の側は ProtectiveStopGuardEntryStateTests が固定する（建玉 0 でも取り消さない・完了させない）。
        store.Save(new ExecutionRecord(
            stop.EntryDecisionId, $"entry-{stop.EntryDecisionId:N}", stop.Symbol, stop.Market, stop.EntrySide,
            stop.ProductType, PositionEffect.Open, stop.Quantity, 1_000m, stop.Quantity, 1_000m, OrderStatus.Filled, 0m,
            Now.AddMinutes(-5)));
        return (new ProtectiveStopGuard(
            broker, broker, contended is null ? stops : contended.Attach(stops), store, new InMemoryOrderReservationStore(),
            new FakeClock(), AlwaysOpenSessionGate.Instance, closeLock: closeLock), broker, stops, store);
    }

    // #1449（PR #1450 の AI レビュー 🟡）: 楽観並行の保存（TrySave）だけを衝突させ続けられるストア（他は内側へ委ねる）。
    private sealed class ContendedStopStore : IProtectiveStopOrderStore
    {
        private InMemoryProtectiveStopOrderStore _inner = null!;
        public bool AlwaysConflict { get; set; }
        public int ConflictCount { get; private set; }

        public ContendedStopStore Attach(InMemoryProtectiveStopOrderStore inner)
        {
            _inner = inner;
            return this;
        }

        public void Save(ProtectiveStopOrder stop) => _inner.Save(stop);

        public bool TrySave(ProtectiveStopOrder stop)
        {
            if (!AlwaysConflict)
                return _inner.TrySave(stop);
            ConflictCount++;
            return false;
        }

        public ProtectiveStopOrder? Find(Guid entryDecisionId) => _inner.Find(entryDecisionId);
        public IReadOnlyList<ProtectiveStopOrder> FindActive(int batchSize) => _inner.FindActive(batchSize);
        public IReadOnlyList<ProtectiveStopOrder> FindActiveFor(string symbol, Market market, TradeSide entrySide) =>
            _inner.FindActiveFor(symbol, market, entrySide);
        public IReadOnlyList<ProtectiveStopOrder> FindOpenFor(string symbol, Market market, TradeSide entrySide) =>
            _inner.FindOpenFor(symbol, market, entrySide);
        public IReadOnlyList<ProtectiveStopOrder> FindActiveSoftwareStops(string symbol, Market market, TradeSide entrySide) =>
            _inner.FindActiveSoftwareStops(symbol, market, entrySide);
        public IReadOnlyList<ProtectiveStopOrder> FindRecentFor(string symbol, Market market, TradeSide entrySide, int limit) =>
            _inner.FindRecentFor(symbol, market, entrySide, limit);
        public IReadOnlyList<ProtectiveStopOrder> FindCompletedSoftwareStops(
            string symbol, Market market, TradeSide entrySide, int limit) =>
            _inner.FindCompletedSoftwareStops(symbol, market, entrySide, limit);
        public IReadOnlyList<ProtectiveStopOrder> FindUnattributedNotified(int limit) => _inner.FindUnattributedNotified(limit);
    }

    private static BrokerPositionSnapshot Long(int qty) => new("AAPL", Market.UnitedStates, qty, 1_000m);
    private static BrokerPositionSnapshot Short(int qty) => new("AAPL", Market.UnitedStates, -qty, 1_000m);

    // ---- 分岐表（境界値テーブル） ----

    [Fact]
    public async Task 建玉あり_逆指値滞留中は何もしない()
    {
        var stop = ActiveStop();
        var (guard, broker, stops, _) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Accepted);
        broker.Positions = [Long(10)];

        var result = await guard.RunOnceAsync(10);

        result.StillActive.Should().Be(1);
        broker.Cancelled.Should().BeEmpty();
        broker.StopPlaceCount.Should().Be(0);
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }

    [Fact]
    public async Task 建玉が消滅したら残存逆指値を取り消す()
    {
        // 決済済みの建玉に残る逆指値が発火すると反対方向の建玉を生む（業務フロー 02 補足）。
        var stop = ActiveStop();
        var (guard, broker, stops, _) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Accepted);
        broker.Positions = []; // 建玉なし

        var result = await guard.RunOnceAsync(10);

        broker.Cancelled.Should().ContainSingle().Which.Should().Be("stop-1");
        result.Completed.Should().Be(1);
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // 🔴 T-10-3012, FR-10, UC-02, #1449（親 #1423 L-2）, IADR-0576 決定2: **取消を照会で確かめられないうちは記録を閉じない。**
    // 是正前は CancelOrderAsync が返った直後に完了させていた——取消が効いていない（届いていない・取消待ち）逆指値を誰も見なくなり、
    // 発火すれば反対建玉になる。照会が非終端（取消待ち）・照会不能のどちらでも Active のまま次の巡回で確かめ直し、終端が見えた巡回で閉じる。
    [Theory]
    [InlineData(OrderStatus.Accepted, false)]        // 照会では生きたまま（取消待ち）
    [InlineData(OrderStatus.Accepted, true)]         // 照会不能（null）
    [InlineData(OrderStatus.PartiallyFilled, false)] // 一部約定のまま取消待ち
    public async Task 建玉が消えた逆指値の取消を照会で確かめられないうちは記録を完了させない(OrderStatus working, bool queryUnavailable)
    {
        var stop = ActiveStop();
        var (guard, broker, stops, _) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(working);
        broker.Positions = [];
        broker.CancelLeavesWorking = true;
        if (queryUnavailable)
            broker.AfterCancel = () => broker.Orders["stop-1"] = null;

        var first = await guard.RunOnceAsync(10);

        broker.Cancelled.Should().Equal("stop-1");
        first.Completed.Should().Be(0, "取消を確かめていない逆指値の記録を閉じない");
        first.Unknown.Should().Be(1);
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);

        // 次の巡回: 逆指値はまだ生きている（照会できるようになった）→ 取り消し直し、今度は終端（取消済み）が見えて閉じる。
        broker.AfterCancel = null;
        broker.CancelLeavesWorking = false;
        broker.Orders["stop-1"] = StopOrder(working);

        var second = await guard.RunOnceAsync(10);

        broker.Cancelled.Should().Equal("stop-1", "stop-1");
        second.Completed.Should().Be(1);
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        broker.StopPlaceCount.Should().Be(0);
        broker.MarketCloseCount.Should().Be(0);
    }

    // 🔴 T-10-3013, FR-10, UC-02, #1449（親 #1423 L-2）, IADR-0576 決定2: **取消の窓で約定した逆指値は約定として扱う。**
    // 取消は「約定済み」で失敗する（ポートの契約の InvalidOperationException）が、照会して約定を確かめ、レグの記録を約定追跡の窓へ戻してから閉じる
    // （台帳へは約定追跡が届ける）。是正前は取消の例外で巡回の 1 件が落ち、約定の扱いは次の巡回まで持ち越されていた。
    [Fact]
    public async Task 建玉が消えた逆指値が取消の窓で約定したら約定として扱って完了する()
    {
        var stop = ActiveStop();
        var (guard, broker, stops, store) = NewGuard(stop);
        store.Save(new ExecutionRecord(
            stop.StopDecisionId, "stop-1", stop.Symbol, stop.Market, TradeSide.Sell, stop.ProductType, PositionEffect.Close,
            10, 950m, 0, 0m, OrderStatus.Accepted, 0m, Now.AddDays(-2)));
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Accepted);
        broker.Positions = [];
        broker.FillsBeforeCancel = true;

        var result = await guard.RunOnceAsync(10);

        broker.Cancelled.Should().Equal("stop-1");
        result.Failed.Should().Be(0, "取消の「約定済み」は照会で確かめる（巡回の失敗にしない）");
        result.Completed.Should().Be(1);
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        store.FindByDecisionId(stop.StopDecisionId)!.ExecutedAt.Should().Be(
            Now, "約定したレグの記録を約定追跡の窓へ戻す（台帳へ届ける）");
        broker.StopPlaceCount.Should().Be(0);
        broker.MarketCloseCount.Should().Be(0);
    }

    // 🔴 T-10-3014, FR-10, #1449（親 #1423 L-2）, IADR-0576 決定3: **並行に進んだ保護記録を古い写しで上書きしない。**
    // (a) 取消を確かめている間に別の書き手がレグを置き換えた → 古い観測で新しいレグを閉じない（記録は新しいレグのまま Active）。
    // (b) 張り直しの間に別の書き手が他の列（通知・失敗の記録）を書いた → 張り直しの列だけを当て直し、その列は残す。
    // 是正前の Save（無条件の上書き）は (a) で新しいレグを Completed＋古い注文 ID へ巻き戻し、(b) でその列を消していた。
    [Fact]
    public async Task 取消を確かめる間に記録が並行に進んだら古い写しで完了させない()
    {
        var stop = ActiveStop();
        var (guard, broker, stops, _) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Accepted);
        broker.Positions = [];
        var replacedLeg = Guid.NewGuid();
        broker.AfterCancel = () =>
            stops.TrySave(stops.Find(stop.EntryDecisionId)! with
            {
                StopDecisionId = replacedLeg,
                StopOrderId = "stop-other",
                Attempt = 2,
            }).Should().BeTrue();

        var result = await guard.RunOnceAsync(10);

        result.Completed.Should().Be(0);
        var row = stops.Find(stop.EntryDecisionId)!;
        row.State.Should().Be(ProtectiveStopState.Active, "古い観測（stop-1 の取消）で新しいレグの記録を閉じない");
        row.StopOrderId.Should().Be("stop-other");
        row.StopDecisionId.Should().Be(replacedLeg);
        row.Attempt.Should().Be(2);
    }

    [Fact]
    public async Task 張り直しの間に並行に書かれた列は張り直しの記録で巻き戻さない()
    {
        var stop = ActiveStop();
        var (guard, broker, stops, _) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Expired);
        broker.Positions = [Long(10)];
        broker.AfterStopPlace = () =>
            stops.TrySave(stops.Find(stop.EntryDecisionId)! with
            {
                StalledNotifiedAt = Now.AddMinutes(-1),
                CloseFailures = 2,
            }).Should().BeTrue();

        var result = await guard.RunOnceAsync(10);

        result.Replaced.Should().Be(1);
        var row = stops.Find(stop.EntryDecisionId)!;
        row.StopOrderId.Should().Be("stop-re-1", "張り直した注文は記録から落とさない");
        row.Attempt.Should().Be(2);
        row.StalledNotifiedAt.Should().Be(Now.AddMinutes(-1), "並行に書かれた列を古い写しで巻き戻さない");
        row.CloseFailures.Should().Be(2);
    }

    // 🔴 T-10-3016, FR-10, #1449（PR #1450 の AI レビュー 🟡）, IADR-0576 決定3: **ブローカーへの操作の後の記録は、当て直しの衝突が尽きても落とさない。**
    // 張り直した逆指値の注文 ID が記録に無いと、実在する注文を誰も見ない（失効の検知も建玉消滅時の取消も外れる）。衝突が尽きたら最新の行へ当てて無条件に保存する。
    [Fact]
    public async Task 張り直しの記録は当て直しの衝突が尽きても新しい注文IDを記録に残す()
    {
        var stop = ActiveStop();
        var contended = new ContendedStopStore();
        var (guard, broker, stops, _) = NewGuard(stop, contended: contended);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Expired);
        broker.Positions = [Long(10)];
        broker.AfterStopPlace = () => contended.AlwaysConflict = true;

        var result = await guard.RunOnceAsync(10);

        contended.ConflictCount.Should().BeGreaterThanOrEqualTo(ProtectiveStopStoreUpdates.MaxAttempts, "当て直しを使い切ってから無条件の保存へ倒す");
        result.Replaced.Should().Be(1);
        result.Failed.Should().Be(0);
        var row = stops.Find(stop.EntryDecisionId)!;
        row.StopOrderId.Should().Be("stop-re-1", "証券会社に実在する注文を記録から落とさない");
        row.Attempt.Should().Be(2);
        row.State.Should().Be(ProtectiveStopState.Active);
    }

    // 🔴 T-10-3017, FR-10, #1449（PR #1450 の AI レビュー 🟡）, IADR-0576 決定3: **成行で手仕舞えた事実は、記録を閉じられた巡回で 1 回だけ出す。**
    // 成行を送る間に別の書き手がレグの注文 ID を書き換え、1 回目の完了が外れる。次の巡回は同じ試行の記録から閉じ直し、そこで初めて出す。
    [Fact]
    public async Task 成行の手仕舞いの完了が並行更新で外れても保護喪失の事実は合計1回だけ出る()
    {
        var stop = ActiveStop();
        var (guard, broker, stops, _) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Expired);
        broker.Orders["stop-other"] = StopOrder(OrderStatus.Expired) with { OrderId = "stop-other" };
        broker.Positions = [Long(10)];
        broker.RejectReplacement = true;
        broker.BeforeMarketClose = () =>
            stops.TrySave(stops.Find(stop.EntryDecisionId)! with { StopOrderId = "stop-other" }).Should().BeTrue();

        var first = await guard.RunOnceAsync(10);

        broker.MarketCloseCount.Should().Be(1);
        first.ClosedOut.Should().Be(0);
        first.Events.OfType<ProtectiveStopCoverageLost>()
            .Where(e => e.Remediation == ProtectiveStopRemediation.PositionClosed).Should().BeEmpty("閉じられなかった巡回では出さない");
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);

        broker.BeforeMarketClose = null;
        var second = await guard.RunOnceAsync(10);

        broker.MarketCloseCount.Should().Be(1, "同じ試行の記録から閉じ直す（成行を重ねない）");
        second.ClosedOut.Should().Be(1);
        second.Events.OfType<ProtectiveStopCoverageLost>()
            .Where(e => e.Remediation == ProtectiveStopRemediation.PositionClosed).Should().ContainSingle();
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    [Fact]
    public async Task 逆指値が約定したら保護完了として記録する()
    {
        var stop = ActiveStop();
        var (guard, broker, stops, _) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Filled);
        broker.Positions = [];

        var result = await guard.RunOnceAsync(10);

        result.Completed.Should().Be(1);
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        broker.StopPlaceCount.Should().Be(0);
        broker.MarketCloseCount.Should().Be(0);
    }

    [Theory]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Rejected)]
    [InlineData(OrderStatus.Expired)]
    public async Task 失効した逆指値は建玉が残っていれば再発注する(OrderStatus lapsed)
    {
        var stop = ActiveStop();
        var (guard, broker, stops, store) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(lapsed);
        broker.Positions = [Long(10)];

        var result = await guard.RunOnceAsync(10);

        result.Replaced.Should().Be(1);
        broker.StopPlaceCount.Should().Be(1);
        var updated = stops.Find(stop.EntryDecisionId)!;
        updated.State.Should().Be(ProtectiveStopState.Active);
        updated.Attempt.Should().Be(2);
        updated.StopOrderId.Should().Be("stop-re-1");
        // 再発注は決定的な新 DecisionId（試行番号つき）で冪等化され、レグは発注結果として記録される。
        updated.StopDecisionId.Should().Be(ProtectiveStopIds.StopDecisionId(stop.EntryDecisionId, 2));
        store.FindByDecisionId(updated.StopDecisionId).Should().NotBeNull();
        var placed = result.Events.OfType<ProtectiveStopPlaced>().Should().ContainSingle().Which;
        placed.Attempt.Should().Be(2);
    }

    [Fact]
    public async Task 再発注は残存建玉の数量に切り詰める()
    {
        // 部分決済済みの建玉（残 3）に元数量 10 の逆指値を張り直すと過剰決済＝反対建玉のリスクになる。
        var stop = ActiveStop(quantity: 10);
        var (guard, broker, _, _) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Cancelled);
        broker.Positions = [Long(3)];

        await guard.RunOnceAsync(10);

        broker.LastStopIntent!.Quantity.Should().Be(3);
    }

    [Fact]
    public async Task 再発注できなければ成行で手仕舞う()
    {
        var stop = ActiveStop();
        var (guard, broker, stops, store) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Cancelled);
        broker.Positions = [Long(10)];
        broker.RejectReplacement = true;

        var result = await guard.RunOnceAsync(10);

        result.ClosedOut.Should().Be(1);
        broker.MarketCloseCount.Should().Be(1);
        broker.LastCloseIntent!.Side.Should().Be(TradeSide.Sell);
        var lost = result.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle().Which;
        lost.Cause.Should().Be(ProtectiveStopLossCause.LapsedInFlight);
        lost.Remediation.Should().Be(ProtectiveStopRemediation.PositionClosed);
        lost.CloseDecisionId.Should().NotBeNull();
        store.FindByDecisionId(lost.CloseDecisionId!.Value).Should().NotBeNull();
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    [Fact]
    public async Task 手仕舞いも失敗したらNoneで人手対応を求め記録はActiveのまま残す()
    {
        var stop = ActiveStop();
        var (guard, broker, stops, _) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Cancelled);
        broker.Positions = [Long(10)];
        broker.RejectReplacement = true;
        broker.ThrowOnMarketClose = true;

        var result = await guard.RunOnceAsync(10);

        var lost = result.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle().Which;
        lost.Remediation.Should().Be(ProtectiveStopRemediation.None);
        // 次回巡回で再試行できるよう Active のまま残す（黙って完了にしない）。
        // 再試行してよいのは失敗が**確実に未発注**（BrokerUnavailableException）だからである（#848・T-10-409）。
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }

    // 決済の直列化の記録係（取った鍵・握っている間か）。Unavailable なら取れない。
    private sealed class RecordingCloseLock(bool unavailable = false) : IClosePositionLock
    {
        public List<ClosePositionKey> Acquired { get; } = [];

        public bool Held { get; private set; }

        /// <summary>直列化を取れた瞬間に呼ばれる（直列化を待つ間に他の経路が約定した、を作る）。</summary>
        public Action? OnAcquired { get; set; }

        public Task<IAsyncDisposable> AcquireAsync(ClosePositionKey key, CancellationToken cancellationToken)
        {
            if (unavailable)
                throw new ClosePositionLockUnavailableException(key, "試験");
            Acquired.Add(key);
            Held = true;
            OnAcquired?.Invoke();
            return Task.FromResult<IAsyncDisposable>(new Release(this));
        }

        private sealed class Release(RecordingCloseLock owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                owner.Held = false;
                return ValueTask.CompletedTask;
            }
        }
    }

    // 🔴 T-10-3009, NFR, FR-10, #1446（親 #1423 M-2）, IADR-0575 決定 3: ガードの成行の手仕舞いは、同じ建玉の決済の直列化を握ったまま送り、
    // 記録を保存してから外す（後から来る判断の決済・S1 がこの記録を読んで数量を決める）。取れなければ送らず、記録は Active のまま
    // 人手対応（None）を求める（確実に未発注＝手仕舞いの送信に失敗した回と同じ扱い）。逆指値の張り直しは直列化を使わない。
    [Fact]
    public async Task T_10_3009_成行の手仕舞いは決済の直列化の中で送り取れなければ送らない()
    {
        var held = new RecordingCloseLock();
        var stop = ActiveStop();
        var (guard, broker, stops, store) = NewGuard(stop, held);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Cancelled);
        broker.Positions = [Long(10)];
        broker.RejectReplacement = true;
        var heldWhileSending = false;
        broker.BeforeMarketClose = () => heldWhileSending = held.Held;

        var result = await guard.RunOnceAsync(10);

        broker.MarketCloseCount.Should().Be(1);
        heldWhileSending.Should().BeTrue("成行は直列化を握ったまま送る");
        held.Held.Should().BeFalse("送って記録した後に外す");
        held.Acquired.Should().Equal([new ClosePositionKey("AAPL", Market.UnitedStates, TradeSide.Sell)]);
        var lost = result.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle().Which;
        store.FindByDecisionId(lost.CloseDecisionId!.Value).Should().NotBeNull();

        var unavailableStop = ActiveStop();
        var (blocked, blockedBroker, blockedStops, _) = NewGuard(unavailableStop, new RecordingCloseLock(unavailable: true));
        blockedBroker.Orders["stop-1"] = StopOrder(OrderStatus.Cancelled);
        blockedBroker.Positions = [Long(10)];
        blockedBroker.RejectReplacement = true;

        var blockedResult = await blocked.RunOnceAsync(10);

        blockedBroker.MarketCloseCount.Should().Be(0, "🔴 直列化を取れなければ成行を送らない");
        blockedResult.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle()
            .Which.Remediation.Should().Be(ProtectiveStopRemediation.None, "人手対応を求める（無音にしない）");
        blockedStops.Find(unavailableStop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active, "次の巡回で撃ち直す");
    }

    // 🔴 T-10-3011, NFR, FR-10, #1446, IADR-0575 決定 3（2026-10-23 追記）: ガードは直列化を取った後に建玉を照会し直し、
    // 送る数量を min(巡回の先頭の数量, 照会し直した決済方向の建玉) にする。直列化を待つ間に他の経路（判断の決済・S1）が約定していれば、
    //   一部約定（10 → 4 株）→ 4 株だけ送る／全量約定（→ 0 株）→ 送らず記録は Active のまま・保護喪失の通知も出さない／
    //   照会できない → 送らず人手対応（None）。古い数量のまま送ると、押さえない証券会社では超過の売り（反対建玉）になる。
    [Theory]
    [InlineData(4, 4)]
    [InlineData(0, 0)]
    [InlineData(-1, 0)] // -1 = 照会できない（null）
    public async Task T_10_3011_直列化を待つ間に他の決済が約定したら照会し直した数量を超えて送らない(int heldAfterWait, int expectedSent)
    {
        var closeLock = new RecordingCloseLock();
        var stop = ActiveStop();
        var (guard, broker, stops, _) = NewGuard(stop, closeLock);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Cancelled);
        broker.Positions = [Long(10)];
        broker.RejectReplacement = true;
        closeLock.OnAcquired = () => broker.Positions = heldAfterWait switch
        {
            < 0 => null,
            0 => [],
            _ => [Long(heldAfterWait)],
        };

        var result = await guard.RunOnceAsync(10);

        broker.MarketCloseCount.Should().Be(expectedSent > 0 ? 1 : 0);
        if (expectedSent > 0)
        {
            broker.LastCloseIntent!.Quantity.Should().Be(expectedSent, "🔴 照会し直した建玉を超えて売らない");
            result.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle()
                .Which.Quantity.Should().Be(expectedSent, "通知・台帳の数量も送った数量");
        }
        else if (heldAfterWait == 0)
        {
            result.Events.OfType<ProtectiveStopCoverageLost>().Should().BeEmpty("建玉はもう無い（次の巡回が完了させる）");
            stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
        }
        else
        {
            result.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle()
                .Which.Remediation.Should().Be(ProtectiveStopRemediation.None, "照会できなければ送らず人手対応を求める");
            stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
        }

        closeLock.Held.Should().BeFalse();
    }

    // ---- #1448, IADR-0577: ガードの成行の手仕舞いは、板に残る判断の決済を取り消して確かめてから送る ----

    private const string DecisionCloseOrderId = "decision-close-1";

    // 判断の全量の手仕舞い（指値・出どころ TradeDecision・未約定）を記録と証券会社の板に置く。
    private static ExecutionRecord PlaceDecisionClose(
        GuardBroker broker, InMemoryExecutedOrderStore store, int quantity = 10,
        OrderApprovalOrigin? origin = OrderApprovalOrigin.TradeDecision, Guid? decisionId = null)
    {
        var record = new ExecutionRecord(
            decisionId ?? Guid.NewGuid(), DecisionCloseOrderId, "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            PositionEffect.Close, quantity, 1_010m, 0, 0m, OrderStatus.Accepted, 0m, Now.AddMinutes(-1), ApprovalOrigin: origin);
        store.Save(record);
        broker.Orders[DecisionCloseOrderId] = new BrokerOrder(
            DecisionCloseOrderId,
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
                quantity, 1_010m, PositionEffect.Close),
            OrderStatus.Accepted, 0, 0m, Now, null);
        return record;
    }

    // 板に残る（取り消されていない・終端でない）判断の決済の未約定の残り＋約定済みの分＋ガードの成行。未約定の売りを押さえない証券会社では、
    // これが建玉を超えると両方が約定して反対建玉になる。
    private static int TotalSells(GuardBroker broker)
    {
        var decision = broker.Orders[DecisionCloseOrderId]!;
        var decisionSells = OrderStatusLifecycle.IsTerminal(decision.Status) ? decision.FilledQuantity : decision.Intent.Quantity;
        return decisionSells + (broker.MarketCloseCount > 0 ? broker.LastCloseIntent!.Quantity : 0);
    }

    // 🔴 T-10-3018（#1448 受け入れ基準 1）, FR-10, ADR-0050 決定1, IADR-0577: 判断の決済（指値・10 株）が板に未約定で残る状態で、
    // 逆指値が失効し張り直しも拒否された。ガードは判断の決済を取り消し、終端を確かめてから成行 10 株を送る。決済の合計は建玉 10 株を超えない。
    // 変異（取消の段を外す）: 判断の決済 10 株＋成行 10 株＝20 株で赤。
    [Fact]
    public async Task T_10_3018_板に残る判断の決済を取り消してから成行を送り合計が建玉を超えない()
    {
        var stop = ActiveStop();
        var (guard, broker, stops, store) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Cancelled);
        broker.Positions = [Long(10)];
        broker.RejectReplacement = true;
        PlaceDecisionClose(broker, store);
        var cancelledBeforeSend = false;
        broker.BeforeMarketClose = () => cancelledBeforeSend = broker.Orders[DecisionCloseOrderId]!.Status == OrderStatus.Cancelled;

        var result = await guard.RunOnceAsync(10);

        broker.Cancelled.Should().Contain(DecisionCloseOrderId, "🔴 板に残る判断の決済を取り消す");
        cancelledBeforeSend.Should().BeTrue("取消を照会で確かめてから成行を送る");
        broker.MarketCloseCount.Should().Be(1);
        broker.LastCloseIntent!.Quantity.Should().Be(10);
        TotalSells(broker).Should().BeLessThanOrEqualTo(10, "🔴 決済の合計が建玉を超えない（押さえない証券会社で反対建玉にしない）");
        ((IExecutedOrderStore)store).FindSoftwareStopCancelRequestedAt(DecisionCloseOrderId).Should().NotBeNull(
            "成行に置き換えるための取消の印（手仕舞いが約定せず終了の通知を出さない）");
        result.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle()
            .Which.Remediation.Should().NotBe(ProtectiveStopRemediation.None);
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // 🔴 T-10-3019（#1448 受け入れ基準 2）, IADR-0577: 取消を受け付けても照会では生きたまま／取消の後に照会できない → 成行を送らず人手対応（None）。
    // 記録は Active のまま（次の巡回で取り消し直す）。#1447 の「照会できなければ送らない」と同じ倒し方。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T_10_3019_判断の決済の取消を確かめられなければ成行を送らず人手対応にする(bool stillWorking)
    {
        var stop = ActiveStop();
        var (guard, broker, stops, store) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Cancelled);
        broker.Positions = [Long(10)];
        broker.RejectReplacement = true;
        PlaceDecisionClose(broker, store);
        if (stillWorking)
            broker.CancelLeavesWorking = true;
        else
            broker.AfterCancel = () => broker.Orders.Remove(DecisionCloseOrderId); // 取消の後は照会不能（null）

        var result = await guard.RunOnceAsync(10);

        broker.Cancelled.Should().Contain(DecisionCloseOrderId);
        broker.MarketCloseCount.Should().Be(0, "🔴 取消を確かめられなければ成行を送らない");
        result.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle()
            .Which.Remediation.Should().Be(ProtectiveStopRemediation.None, "人手対応を求める（無音にしない）");
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active, "次の巡回で取り消し直す");
    }

    // 🔴 T-10-3020（#1448 受け入れ基準 3）, IADR-0577: 取消の窓で判断の決済が一部（4 株）約定して取り消された → 建玉 6 株を照会し直して 6 株だけ送る。
    // 全部約定した → 建玉 0 で送らず、記録は Active のまま（次の巡回が建玉 0 を見て完了させる）・保護喪失の通知も出さない。
    // 変異（取消の後の照会し直しを外す）: 一部約定で 10 株を送り赤。
    [Theory]
    [InlineData(4, 6)]
    [InlineData(10, 0)]
    public async Task T_10_3020_取消の窓で判断の決済が約定したら照会し直した数量で送り0なら送らない(int filledInWindow, int expectedSent)
    {
        var stop = ActiveStop();
        var (guard, broker, stops, store) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Cancelled);
        broker.Positions = [Long(10)];
        broker.RejectReplacement = true;
        PlaceDecisionClose(broker, store);
        broker.AfterCancel = () =>
        {
            var order = broker.Orders[DecisionCloseOrderId]!;
            broker.Orders[DecisionCloseOrderId] = order with
            {
                Status = filledInWindow >= order.Intent.Quantity ? OrderStatus.Filled : OrderStatus.Cancelled,
                FilledQuantity = filledInWindow,
                AveragePrice = 1_010m,
                CompletedAt = Now,
            };
            var left = 10 - filledInWindow;
            broker.Positions = left > 0 ? [Long(left)] : [];
        };

        var result = await guard.RunOnceAsync(10);

        broker.MarketCloseCount.Should().Be(expectedSent > 0 ? 1 : 0);
        TotalSells(broker).Should().BeLessThanOrEqualTo(10, "🔴 判断の決済の約定分と成行の合計が建玉を超えない");
        if (expectedSent > 0)
        {
            broker.LastCloseIntent!.Quantity.Should().Be(expectedSent, "🔴 取消の窓の約定を差し引いた建玉で送る");
            result.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle()
                .Which.Quantity.Should().Be(expectedSent);
        }
        else
        {
            result.Events.OfType<ProtectiveStopCoverageLost>().Should().BeEmpty("建玉はもう無い（次の巡回が完了させる）");
            stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
        }
    }

    // T-10-3021（#1448 受け入れ基準 4・退行なし）, IADR-0577: 判断の決済が無い／記録は非終端のまま古いが証券会社では既に終わっている
    // （約定は進んでいない）／板に残るのが猶予の内の利用者の手仕舞い（OwnerClose。IADR-0515 決定3）だけ
    // → 何も取り消さず、従来どおり全量を送る（利用者の決済の差し引きは #1452 で扱う）。
    public enum PendingCloseCase
    {
        None,
        StaleTerminalRecord,
        OwnerCloseInGrace,
    }

    [Theory]
    [InlineData(PendingCloseCase.None)]
    [InlineData(PendingCloseCase.StaleTerminalRecord)]
    [InlineData(PendingCloseCase.OwnerCloseInGrace)]
    public async Task T_10_3021_判断の決済が無ければ取り消さず従来どおり全量を送る(PendingCloseCase pending)
    {
        var stop = ActiveStop();
        var (guard, broker, _, store) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Cancelled);
        broker.Positions = [Long(10)];
        broker.RejectReplacement = true;
        switch (pending)
        {
            case PendingCloseCase.StaleTerminalRecord:
                PlaceDecisionClose(broker, store);
                broker.Orders[DecisionCloseOrderId] = broker.Orders[DecisionCloseOrderId]! with
                {
                    Status = OrderStatus.Cancelled,
                    CompletedAt = Now,
                };
                break;
            case PendingCloseCase.OwnerCloseInGrace:
                // 記録の時刻は 1 分前（猶予 NettedCloseGrace＝2 分の内）。証券会社では生きている。
                PlaceDecisionClose(broker, store, origin: OrderApprovalOrigin.OwnerClose);
                break;
        }

        await guard.RunOnceAsync(10);

        broker.Cancelled.Should().BeEmpty("取り消すものが無い（猶予の内の利用者の手仕舞いは取り消さない）");
        broker.MarketCloseCount.Should().Be(1);
        broker.LastCloseIntent!.Quantity.Should().Be(10, "従来どおり全量を送る");
    }

    [Fact]
    public async Task 失効かつ建玉なしは完了として記録する()
    {
        var stop = ActiveStop();
        var (guard, broker, stops, _) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Cancelled);
        broker.Positions = [];

        var result = await guard.RunOnceAsync(10);

        result.Completed.Should().Be(1);
        broker.StopPlaceCount.Should().Be(0);
        broker.MarketCloseCount.Should().Be(0);
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // ---- fail-safe（否定形: 不明を「無い」と取り違えない） ----

    [Fact]
    public async Task 注文照会が不能なら据え置く_否定形()
    {
        var stop = ActiveStop();
        var (guard, broker, stops, _) = NewGuard(stop);
        // Orders 未登録 = 照会不能（null）。
        broker.Positions = [];

        var result = await guard.RunOnceAsync(10);

        result.Unknown.Should().Be(1);
        broker.Cancelled.Should().BeEmpty("状態不明の逆指値を取り消してはならない");
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }

    [Fact]
    public async Task 建玉照会が不能なら巡回ごと据え置く_否定形()
    {
        var stop = ActiveStop();
        var (guard, broker, stops, _) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Accepted);
        broker.Positions = null; // 照会不能

        var result = await guard.RunOnceAsync(10);

        result.Unknown.Should().Be(1);
        broker.Cancelled.Should().BeEmpty("建玉不明のまま「消滅した」と誤認して逆指値を消してはならない");
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }

    // ---- 方向（ショート）と数量の符号 ----

    [Fact]
    public async Task ショート建玉の残はマイナス数量から求める()
    {
        var stop = ActiveStop(entrySide: TradeSide.Sell);
        var (guard, broker, _, _) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Cancelled);
        broker.Positions = [Short(10)];

        var result = await guard.RunOnceAsync(10);

        result.Replaced.Should().Be(1, "ショート建玉（数量 −10）が残っている＝保護が要る");
        broker.LastStopIntent!.Side.Should().Be(TradeSide.Buy, "ショートの決済は買戻し");
    }

    [Fact]
    public void 残数量の算定はエントリー方向に一致する建玉だけを数える()
    {
        var longStop = ActiveStop(entrySide: TradeSide.Buy);
        var shortStop = ActiveStop(entrySide: TradeSide.Sell);

        // 境界値: ロング残 0 ↔ 1、ショート残 0 ↔ 1、反対方向は 0 と数える。
        ProtectiveStopGuard.RemainingPositionFor(longStop, [Long(1)]).Should().Be(1);
        ProtectiveStopGuard.RemainingPositionFor(longStop, []).Should().Be(0);
        ProtectiveStopGuard.RemainingPositionFor(longStop, [Short(5)]).Should().Be(0);
        ProtectiveStopGuard.RemainingPositionFor(shortStop, [Short(1)]).Should().Be(1);
        ProtectiveStopGuard.RemainingPositionFor(shortStop, [Long(5)]).Should().Be(0);
        ProtectiveStopGuard.RemainingPositionFor(shortStop, []).Should().Be(0);
    }

    // ---- バッチの fail-safe（1 件の異常で巡回全体を止めない） ----

    [Fact]
    public async Task 巡回対象が無ければ建玉照会もしない_否定形()
    {
        // 保護中の建玉がゼロのときに毎回 OpenD へ建玉照会を投げると、無駄な往復が常時走る。
        // 「対象ゼロなら何もしない」を固定する（結果は空のサマリ）。
        var broker = new GuardBroker { Positions = null };
        var guard = new ProtectiveStopGuard(
            broker, broker, new InMemoryProtectiveStopOrderStore(), new InMemoryExecutedOrderStore(),
            new InMemoryOrderReservationStore(), new FakeClock(), AlwaysOpenSessionGate.Instance);

        var result = await guard.RunOnceAsync(10);

        result.Should().Be(ProtectiveStopGuardResult.Empty);
        result.Scanned.Should().Be(0);
        result.Failed.Should().Be(0);
        result.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task 一件の評価が例外でも残りの件は処理し失敗として数える()
    {
        // 1 銘柄のブローカ照会が壊れたとき、巡回全体が中断すると**他の建玉の保護喪失が検知されなくなる**。
        // 失敗は件数へ計上し（可観測性）、残りの評価は続ける。
        var broken = ActiveStop(stopOrderId: "stop-broken");
        var healthy = ActiveStop(stopOrderId: "stop-ok");
        var broker = new GuardBroker
        {
            Positions = [Long(10)],
        };
        broker.ThrowOnGetOrderIds.Add("stop-broken");
        broker.Orders["stop-ok"] = StopOrder(OrderStatus.Accepted);
        var stops = new InMemoryProtectiveStopOrderStore();
        stops.Save(broken);
        stops.Save(healthy);
        var guard = new ProtectiveStopGuard(
            broker, broker, stops, new InMemoryExecutedOrderStore(), new InMemoryOrderReservationStore(), new FakeClock(), AlwaysOpenSessionGate.Instance);

        var result = await guard.RunOnceAsync(10);

        result.Scanned.Should().Be(2);
        result.Failed.Should().Be(1, "壊れた 1 件だけを失敗として数える");
        result.StillActive.Should().Be(1, "残りの 1 件は評価され続ける");
    }

    [Fact]
    public async Task 再発注の送信が例外でも手仕舞いへ倒れる()
    {
        // 「再発注の**拒否**」だけでなく「再発注の**送信失敗**（接続断など）」も、逆指値なしの建玉を
        // 残さない側へ倒れなければならない。例外がそのまま抜けると建玉が無防備なまま残る。
        var stop = ActiveStop();
        var (guard, broker, stops, store) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Cancelled);
        broker.Positions = [Long(10)];
        broker.ThrowOnStopPlace = true;

        var result = await guard.RunOnceAsync(10);

        result.ClosedOut.Should().Be(1);
        broker.MarketCloseCount.Should().Be(1, "再発注が送れなければ成行で手仕舞う");
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        store.FindByDecisionId(ProtectiveStopIds.CloseDecisionId(stop.EntryDecisionId, attempt: 2))
            .Should().NotBeNull("手仕舞いレグは約定追跡へ載る");
        result.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle(e =>
            e.Remediation == ProtectiveStopRemediation.PositionClosed);
    }

    // ---- T-10-1739, FR-10, #1093, IADR-0458: 建玉照会の一時的な失敗は巡回の中で 1 回だけ照会し直す ----

    private static ProtectiveStopGuard WithRetry(GuardBroker broker, InMemoryProtectiveStopOrderStore stops,
        InMemoryExecutedOrderStore store, List<TimeSpan> waits) =>
        new(broker, broker, stops, store, new InMemoryOrderReservationStore(), new FakeClock(), AlwaysOpenSessionGate.Instance,
            positionQueryRetry: new PositionQueryRetry(
                new ProtectiveStopGuardOptions(),
                (wait, _) =>
                {
                    waits.Add(wait);
                    return Task.CompletedTask;
                },
                () => 0.5));

    [Fact]
    public async Task 建玉照会が一時的に失敗しても照会し直して成功すれば_この巡回で評価する()
    {
        var stop = ActiveStop();
        var (_, broker, stops, store) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Accepted);
        broker.Positions = [Long(10)];
        broker.ClassifiedResults.Enqueue(PositionQueryResult.Failed(PositionQueryFailure.Transient));
        var waits = new List<TimeSpan>();

        var result = await WithRetry(broker, stops, store, waits).RunOnceAsync(10);

        result.Unknown.Should().Be(0, "照会し直して建玉が取れたので据え置かない");
        result.StillActive.Should().Be(1);
        broker.ClassifiedQueryCount.Should().Be(2);
        waits.Should().Equal(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task 建玉照会が分類できない失敗なら照会し直さず_従来どおり全件据え置く()
    {
        var stop = ActiveStop();
        var (_, broker, stops, store) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Accepted);
        broker.Positions = [Long(10)];
        broker.ClassifiedResults.Enqueue(PositionQueryResult.Failed(PositionQueryFailure.Other));
        var waits = new List<TimeSpan>();

        var result = await WithRetry(broker, stops, store, waits).RunOnceAsync(10);

        result.Unknown.Should().Be(1);
        broker.ClassifiedQueryCount.Should().Be(1);
        waits.Should().BeEmpty();
        broker.Cancelled.Should().BeEmpty();
        broker.StopPlaceCount.Should().Be(0);
    }

    [Fact]
    public async Task 照会し直しを渡さないガードは分類つきの口を使わない()
    {
        var stop = ActiveStop();
        var (guard, broker, _, _) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Accepted);
        broker.Positions = [Long(10)];

        await guard.RunOnceAsync(10);

        broker.ClassifiedQueryCount.Should().Be(0);
        broker.PositionQueryCount.Should().Be(1);
    }

    // ---- T-10-1770, NFR, FR-10, #1092, IADR-0462 決定2: 巡回の先頭の建玉照会の成否を、失敗の種類つきで報告する ----

    private static ProtectiveStopGuard WithHealth(
        GuardBroker broker, InMemoryProtectiveStopOrderStore stops, InMemoryExecutedOrderStore store,
        RecordingPositionQueryHealth health, bool withRetry) =>
        new(broker, broker, stops, store, new InMemoryOrderReservationStore(), new FakeClock(), AlwaysOpenSessionGate.Instance,
            positionQueryRetry: withRetry
                ? new PositionQueryRetry(new ProtectiveStopGuardOptions(), (_, _) => Task.CompletedTask, () => 0.5)
                : null,
            positionQueryHealth: health);

    [Theory]
    // 照会し直しの最終結果を 1 回の観測として報告する（途中の失敗は観測として数えない）。
    // T-10-2213〜T-10-2215, FR-10, NFR, #1164, IADR-0487 決定2: 照会し直した失敗は「最初→最後」で載せる（照会し直しの有無を台帳で読む）。
    [InlineData(new[] { PositionQueryFailure.Transient }, true, null)] // T-10-2215（P3）: 照会し直して成功
    [InlineData(new[] { PositionQueryFailure.Transient, PositionQueryFailure.RateLimited }, false, "Transient→RateLimited")]
    [InlineData(new[] { PositionQueryFailure.Transient, PositionQueryFailure.Other }, false, "Transient→Other")] // T-10-2213（P1）
    [InlineData(new[] { PositionQueryFailure.Other }, false, "Other")] // T-10-2214（P2）: 照会し直さない
    public async Task T_10_1770_ガードは照会し直しの最終結果を失敗の種類つきで1回だけ報告する(
        PositionQueryFailure[] failures, bool succeeded, string? kind)
    {
        var stop = ActiveStop();
        var (_, broker, stops, store) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Accepted);
        broker.Positions = [Long(10)];
        foreach (var f in failures)
            broker.ClassifiedResults.Enqueue(PositionQueryResult.Failed(f));
        var health = new RecordingPositionQueryHealth();

        await WithHealth(broker, stops, store, health, withRetry: true).RunOnceAsync(10);

        health.Reports.Should().Equal([(PositionQuerySource.ProtectiveStopGuard, succeeded, kind)]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T_10_1770_照会し直しの無いガードも成否を報告し_失敗の種類は不明とする(bool succeeded)
    {
        var stop = ActiveStop();
        var (_, broker, stops, store) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Accepted);
        broker.Positions = succeeded ? [Long(10)] : null;
        var health = new RecordingPositionQueryHealth();

        await WithHealth(broker, stops, store, health, withRetry: false).RunOnceAsync(10);

        health.Reports.Should().Equal([(PositionQuerySource.ProtectiveStopGuard, succeeded, (string?)null)]);
    }

    [Fact]
    public async Task T_10_1770_巡回対象が無ければ照会せず報告もしない_建玉0の確かめ直しは数えない()
    {
        var health = new RecordingPositionQueryHealth();
        var empty = new GuardBroker();
        await WithHealth(empty, new InMemoryProtectiveStopOrderStore(), new InMemoryExecutedOrderStore(), health, false)
            .RunOnceAsync(10);
        health.Reports.Should().BeEmpty("Active な保護記録が無い巡回は照会しない");

        // 建玉 0 ＋ エントリーは約定済み → 取り消す前に建玉を照会し直す（2 回目の照会）。報告は巡回の先頭の 1 回だけ。
        var stop = ActiveStop();
        var (_, broker, stops, store) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Accepted);
        broker.Positions = [];

        await WithHealth(broker, stops, store, health, withRetry: false).RunOnceAsync(10);

        broker.PositionQueryCount.Should().Be(2, "前提: 建玉 0 の確かめ直しで 2 回照会している");
        health.Reports.Should().Equal([(PositionQuerySource.ProtectiveStopGuard, true, (string?)null)]);
    }

    // T-10-1770: PR #1110 の監査 N2。巡回の先頭の照会の最中にガードが止められた（キャンセル）ときは、照会の失敗として報告しない。
    // キャンセルは moomoo の不調ではない（停止・再配備のたびに Failing を台帳へ出すと、夜間の要約に実在しない失敗の区間が現れる）。
    // キャンセルはそのまま伝える（据え置きの結果へ縮退しない）。照会し直しの有無の両方で固定する。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T_10_1770_照会の最中のキャンセルは失敗として報告せず_キャンセルを伝える(bool withRetry)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stop = ActiveStop();
        var (_, broker, stops, store) = NewGuard(stop);
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Accepted);
        broker.ThrowOnPositionQuery = () =>
        {
            cts.Cancel();
            return new OperationCanceledException(cts.Token);
        };
        var health = new RecordingPositionQueryHealth();

        var act = () => WithHealth(broker, stops, store, health, withRetry).RunOnceAsync(10, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (broker.ClassifiedQueryCount + broker.PositionQueryCount).Should().Be(1, "前提: 巡回の先頭の照会の最中に取り消された");
        health.Reports.Should().BeEmpty("キャンセルを照会の失敗として数えない");
    }
}
