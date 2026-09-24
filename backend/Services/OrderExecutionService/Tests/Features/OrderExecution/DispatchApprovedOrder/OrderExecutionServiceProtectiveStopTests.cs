using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// FR-10, UC-02, ADR-0016 決定2(b), #331, IADR-0210: 保護逆指値の同時発注と「逆指値なしの建玉を持たない」
// （未受理時の建玉解消/不成立の全分岐）の検証。issue #331 受け入れ基準 1 のテスト群。
// 統制系の 3 点セット: 境界値テーブル（分岐表）＋プロパティベース（不変条件）＋否定形（Close は逆指値を張らない等）。
public class OrderExecutionServiceProtectiveStopTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 6, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    public enum StopBehavior { Accept, Reject, Throw, Unavailable }

    // #848, IADR-0117（改定 7）: Unavailable＝確実に未発注（接続確立の失敗）。Throw＝分類できない例外
    //（未発注と言い切れない）。**値は末尾へ足している**（不変条件テストの疑似乱数は先頭 2 値から引く）。
    // #857, IADR-0369: Reject＝**確認できた拒否**（終端が「返る」。例外ではない・建玉は残っている）。
    public enum RemedyBehavior { Succeed, Throw, Unavailable, Indeterminate, Reject }

    // エントリー・逆指値・取消・成行手仕舞いの挙動を分岐単位で注入できるブローカ。
    private sealed class ScriptedBroker : IBrokerAdapter, IProtectiveOrderBroker
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public OrderStatus EntryStatus { get; init; } = OrderStatus.Accepted;
        public int EntryFilled { get; init; }
        public StopBehavior Stop { get; init; } = StopBehavior.Accept;
        public RemedyBehavior Cancel { get; init; } = RemedyBehavior.Succeed;
        public RemedyBehavior MarketClose { get; init; } = RemedyBehavior.Succeed;

        /// <summary>取消失敗後の再照会が返す約定数（null＝照会も失敗）。</summary>
        public int? RequeryFilled { get; init; }

        public int StopPlaceCount { get; private set; }
        public int CancelCount { get; private set; }
        public int MarketCloseCount { get; private set; }
        public OrderIntent? MarketCloseIntent { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            Task.FromResult(new BrokerOrder(
                "entry-1", intent, EntryStatus, EntryFilled, EntryFilled > 0 ? intent.Price : 0m, Now,
                CompletedAt: EntryStatus == OrderStatus.Filled ? Now : null));

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default)
        {
            StopPlaceCount++;
            return Stop switch
            {
                StopBehavior.Accept => Task.FromResult(new BrokerOrder(
                    "stop-1", closeIntent, OrderStatus.Accepted, 0, 0m, Now, null)),
                StopBehavior.Reject => Task.FromResult(new BrokerOrder(
                    "stop-1", closeIntent, OrderStatus.Rejected, 0, 0m, Now, Now)),
                StopBehavior.Unavailable => throw new BrokerUnavailableException("OpenD 切断（テスト）"),
                _ => throw new InvalidOperationException("逆指値の発注に失敗（テスト）"),
            };
        }

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            MarketCloseCount++;
            MarketCloseIntent = closeIntent;
            return MarketClose switch
            {
                RemedyBehavior.Succeed => Task.FromResult(new BrokerOrder(
                    "close-1", closeIntent, OrderStatus.Filled, closeIntent.Quantity, closeIntent.Price, Now, Now)),
                // #857: 発注前検証の棄却・OpenD の retType != 0 は、終端 Rejected が**返る**（例外にならない）。
                RemedyBehavior.Reject => Task.FromResult(new BrokerOrder(
                    "close-1", closeIntent, OrderStatus.Rejected, 0, 0m, Now, Now)),
                RemedyBehavior.Unavailable => throw new BrokerUnavailableException("OpenD 切断・成行手仕舞いは未発注（テスト）"),
                RemedyBehavior.Indeterminate => throw new BrokerDispatchIndeterminateException(
                    "moomoo へ発注を送信しましたが結果を確認できませんでした（テスト）"),
                _ => throw new InvalidOperationException("成行手仕舞いに失敗（テスト）"),
            };
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(RequeryFilled is { } filled
                ? new BrokerOrder("entry-1", Intent(), OrderStatus.PartiallyFilled, filled, 1_000m, Now, null)
                : null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            CancelCount++;
            return Cancel == RemedyBehavior.Succeed
                ? Task.CompletedTask
                : throw new InvalidOperationException("取消に失敗（テスト）");
        }
    }

    private static OrderIntent Intent(int qty = 10, decimal? stopLoss = 950m, TradeSide side = TradeSide.Buy) =>
        new("AAPL", Market.UnitedStates, side, ProductType.Cash, BrokerProvider.MoomooSimulate, qty, 1_000m,
            PositionEffect.Open, stopLoss, FxRateToBase: 1m);

    private static (AppSvc Service, InMemoryExecutedOrderStore Store, InMemoryProtectiveStopOrderStore Stops,
        InMemoryOrderReservationStore Reservations) NewService(IBrokerAdapter broker)
    {
        var store = new InMemoryExecutedOrderStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        return (new AppSvc(broker, store, reservations, new FakeClock(), stops), store, stops, reservations);
    }

    private static OrderApproved Approved(OrderIntent intent) => new(Guid.NewGuid(), intent, intent.Quantity, Now);

    // ---- 同時発注（受理） ----

    [Theory]
    [InlineData(OrderStatus.Accepted, 0)]
    [InlineData(OrderStatus.PartiallyFilled, 4)]
    [InlineData(OrderStatus.Filled, 10)]
    public async Task エントリーが生きていれば保護逆指値が同時発注される(OrderStatus entryStatus, int filled)
    {
        var broker = new ScriptedBroker { EntryStatus = entryStatus, EntryFilled = filled };
        var (service, store, stops, _) = NewService(broker);
        var approved = Approved(Intent());

        var result = await service.ExecuteAsync(approved);

        broker.StopPlaceCount.Should().Be(1, "逆指値は建玉と同時に発注する（FR-10・ADR-0016 決定2(b)）");
        var placed = result.StopPlaced!;
        placed.EntryDecisionId.Should().Be(approved.DecisionId);
        placed.TriggerPrice.Should().Be(950m);
        placed.CloseIntent.Side.Should().Be(TradeSide.Sell);
        placed.CloseIntent.PositionEffect.Should().Be(PositionEffect.Close);
        placed.CloseIntent.Quantity.Should().Be(10);
        result.CoverageLost.Should().BeNull();

        // 逆指値レグは発注結果として記録され、約定追跡ポーリング（IADR-0113）の対象になる。
        store.FindByDecisionId(placed.StopDecisionId).Should().NotBeNull();
        stops.Find(approved.DecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }

    [Fact]
    public async Task ショートエントリーには買戻しの逆指値が張られる()
    {
        // ADR-0016 決定2(b): 逆指値の同時発注必須は建玉の方向を問わない。
        var broker = new ScriptedBroker { EntryStatus = OrderStatus.Filled, EntryFilled = 10 };
        var (service, _, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(Intent(side: TradeSide.Sell, stopLoss: 1_050m)));

        result.StopPlaced!.CloseIntent.Side.Should().Be(TradeSide.Buy, "ショートの決済は買戻し");
        result.StopPlaced.TriggerPrice.Should().Be(1_050m);
    }

    [Fact]
    public async Task 逆指値レグはエントリーの換算レートを引き継ぐ()
    {
        // FR-17, IADR-0107: 決済レグの FxRateToBase を落とすと外貨建て決済が未換算で台帳へ積まれる。
        var broker = new ScriptedBroker();
        var (service, _, _, _) = NewService(broker);
        var intent = Intent() with { Market = Market.Japan, FxRateToBase = 0.0068m };

        var result = await service.ExecuteAsync(new OrderApproved(Guid.NewGuid(), intent, intent.Quantity, Now));

        result.StopPlaced!.CloseIntent.FxRateToBase.Should().Be(0.0068m);
    }

    // ---- 未受理時の建玉解消（全分岐・境界値テーブル） ----

    [Fact]
    public async Task 逆指値未受理でエントリー未約定なら取り消す()
    {
        var broker = new ScriptedBroker { EntryStatus = OrderStatus.Accepted, EntryFilled = 0, Stop = StopBehavior.Reject };
        var (service, _, stops, _) = NewService(broker);
        var approved = Approved(Intent());

        var result = await service.ExecuteAsync(approved);

        broker.CancelCount.Should().Be(1, "未約定のエントリーは取り消す（業務フロー 02 の表）");
        broker.MarketCloseCount.Should().Be(0);
        var lost = result.CoverageLost!;
        lost.Cause.Should().Be(ProtectiveStopLossCause.RejectedAtEntry);
        lost.Remediation.Should().Be(ProtectiveStopRemediation.EntryCancelled);
        result.StopPlaced.Should().BeNull();
        stops.Find(approved.DecisionId).Should().BeNull("保護は成立していない");
    }

    [Theory]
    [InlineData(OrderStatus.Filled, 10, 10)]        // 全量約定 → 全量手仕舞い
    [InlineData(OrderStatus.PartiallyFilled, 4, 4)] // 部分約定 → 約定分だけ手仕舞い
    [InlineData(OrderStatus.PartiallyFilled, 1, 1)] // 境界: 最小の約定数
    public async Task 逆指値未受理でエントリー約定済みなら成行で手仕舞う(OrderStatus entryStatus, int filled, int expectedCloseQty)
    {
        var broker = new ScriptedBroker { EntryStatus = entryStatus, EntryFilled = filled, Stop = StopBehavior.Reject };
        var (service, store, _, _) = NewService(broker);
        var approved = Approved(Intent());

        var result = await service.ExecuteAsync(approved);

        broker.MarketCloseCount.Should().Be(1, "約定済みの建玉は即座に成行で手仕舞う（業務フロー 02 の表）");
        broker.MarketCloseIntent!.Quantity.Should().Be(expectedCloseQty);
        broker.MarketCloseIntent.Side.Should().Be(TradeSide.Sell);
        var lost = result.CoverageLost!;
        lost.Remediation.Should().Be(ProtectiveStopRemediation.PositionClosed);
        lost.Quantity.Should().Be(expectedCloseQty);
        lost.CloseDecisionId.Should().NotBeNull("手仕舞いレグは台帳へ結線される");
        store.FindByDecisionId(lost.CloseDecisionId!.Value).Should().NotBeNull();
    }

    [Fact]
    public async Task 取消失敗後に約定が判明したら約定分を手仕舞う()
    {
        // 取消と約定の競合: 取消が失敗＝その間に約定した可能性 → 再照会して約定分を手仕舞いへ回す。
        var broker = new ScriptedBroker
        {
            EntryStatus = OrderStatus.Accepted,
            EntryFilled = 0,
            Stop = StopBehavior.Reject,
            Cancel = RemedyBehavior.Throw,
            RequeryFilled = 6,
        };
        var (service, _, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(Intent()));

        broker.MarketCloseIntent!.Quantity.Should().Be(6);
        result.CoverageLost!.Remediation.Should().Be(ProtectiveStopRemediation.PositionClosed);
    }

    [Fact]
    public async Task 取消も照会も失敗したら人手対応のNoneを返す_否定形()
    {
        // 状態不明のまま自動で注文を重ねない（誤発注の方が危険）。None は Critical 通知で人手対応を求める。
        var broker = new ScriptedBroker
        {
            EntryStatus = OrderStatus.Accepted,
            EntryFilled = 0,
            Stop = StopBehavior.Reject,
            Cancel = RemedyBehavior.Throw,
            RequeryFilled = null,
        };
        var (service, _, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(Intent()));

        result.CoverageLost!.Remediation.Should().Be(ProtectiveStopRemediation.None);
        broker.MarketCloseCount.Should().Be(0, "状態不明のまま注文を重ねない");
    }

    [Fact]
    public async Task 手仕舞いも失敗したらNoneを返す()
    {
        // #848, IADR-0117（改定 7）: None（解消に失敗）と言えるのは**確実に未発注**（接続確立の失敗）のときだけである。
        // 分類できない例外・届いたか不明は CloseDispatchIndeterminate であり、T-10-409 が固定する。
        var broker = new ScriptedBroker
        {
            EntryStatus = OrderStatus.Filled,
            EntryFilled = 10,
            Stop = StopBehavior.Reject,
            MarketClose = RemedyBehavior.Unavailable,
        };
        var (service, _, _, reservations) = NewService(broker);
        var approved = Approved(Intent());

        var result = await service.ExecuteAsync(approved);

        result.CoverageLost!.Remediation.Should().Be(ProtectiveStopRemediation.None);
        result.CoverageLost.CloseDecisionId.Should().BeNull("未発注の手仕舞いレグを台帳へ結線しない");
        reservations.Find(ProtectiveStopIds.CloseDecisionId(approved.DecisionId, attempt: 1))
            .Should().BeNull("確実に未発注なので予約は解放される");
    }

    // 🔴 T-10-409, FR-10, FR-11, UC-06, #848, IADR-0117（2026-09-19 追記・改定 7）:
    // エントリー直後の成行手仕舞いが**届いたか不明**で終わったとき、None（解消に失敗＝未発注）と主張しない。
    // None は手仕舞いレグを運ばないため取引台帳が押さえず、利用者の手仕舞い要求が通って同じ株数に 2 本の決済が並ぶ。
    // 改定 6 以前は偽 ID の Rejected＋PositionClosed で承認行が足され、30 分の窓が押さえていた。
    [Theory]
    [InlineData(RemedyBehavior.Indeterminate)]
    [InlineData(RemedyBehavior.Throw)] // 分類できない例外も未発注とは言い切れない
    public async Task 成行手仕舞いが届いたか不明なら_未発注と主張せず手仕舞いレグを運び予約を据え置く_否定形(RemedyBehavior close)
    {
        var broker = new ScriptedBroker
        {
            EntryStatus = OrderStatus.Filled,
            EntryFilled = 10,
            Stop = StopBehavior.Reject,
            MarketClose = close,
        };
        var (service, store, _, reservations) = NewService(broker);
        var approved = Approved(Intent());
        var closeDecisionId = ProtectiveStopIds.CloseDecisionId(approved.DecisionId, attempt: 1);

        var result = await service.ExecuteAsync(approved);

        broker.MarketCloseCount.Should().Be(1);
        var lost = result.CoverageLost!;
        lost.Remediation.Should().Be(ProtectiveStopRemediation.CloseDispatchIndeterminate);
        lost.CloseDecisionId.Should().Be(closeDecisionId, "台帳が処理中の決済として押さえるために手仕舞いレグを運ぶ");
        lost.CloseIntent.Should().NotBeNull();
        lost.CloseIntent!.Quantity.Should().Be(10);
        lost.CloseIntent.PositionEffect.Should().Be(PositionEffect.Close);

        // 予約は解放も確定もしない。実在しない注文 ID の記録は作らない。
        var reservation = reservations.Find(closeDecisionId);
        reservation.Should().NotBeNull();
        reservation!.State.Should().Be(OrderExecutionService.Features.OrderExecution.OrderDispatchState.Reserved);
        store.FindByDecisionId(closeDecisionId).Should().BeNull();
    }

    private sealed class MutableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = start;
    }

    // ガードは巡回対象（Active な保護記録）が 0 件なら建玉を照会しない。照会されたら数える（されないことの表明）。
    private sealed class CountingPositionSource : IBrokerPositionSource
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<BrokerPositionSnapshot>?>(
                [new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 10, 1_000m)]);
        }
    }

    // 🔴 T-10-751, FR-10, FR-11, UC-06, #941, IADR-0369（2026-09-25 追記）, IADR-0117（改定 9「塞がないもの」）:
    // エントリー時の「届いたか不明」の通知が「この 1 回だけ・巡回しない」と言う（T-10-750）根拠を**コードで**固定する。
    //   - 保護記録を作らない（ガードの巡回対象に入らない）。
    //   - 1 時間後（HeldCloseNotificationTracker の再通知間隔）にガードを巡回させても、イベントは 1 件も出ない
    //     ——1 時間ごとの再通知（T-10-451）はガードが保護記録について行うものである。
    //   - 同じ承認が再配送されても（再起動後の再処理を含む）、相 1 で返り保護喪失を出し直さない・成行も重ねない。
    // どれかが変わったら（例えばこの経路に保護記録が足されたら）通知の文面も変えなければならない。そのときに赤になる。
    [Fact]
    public async Task エントリー時の成行手仕舞いが届いたか不明なら_保護記録は無く_巡回も再配送も通知を出し直さない_否定形()
    {
        var broker = new ScriptedBroker
        {
            EntryStatus = OrderStatus.Filled,
            EntryFilled = 10,
            Stop = StopBehavior.Reject,
            MarketClose = RemedyBehavior.Indeterminate,
        };
        var (service, store, stops, reservations) = NewService(broker);
        var approved = Approved(Intent());

        var first = await service.ExecuteAsync(approved);

        first.CoverageLost!.Cause.Should().Be(ProtectiveStopLossCause.RejectedAtEntry);
        first.CoverageLost.Remediation.Should().Be(ProtectiveStopRemediation.CloseDispatchIndeterminate);
        stops.Find(approved.DecisionId).Should().BeNull("エントリー時の経路は保護記録を作らない");
        stops.FindActive(100).Should().BeEmpty("巡回の対象に入る記録が 1 件も無い");

        // 本番と同じ部品（同じストア・予約・再通知の記憶）でガードを 1 時間後に巡回させる。
        var clock = new MutableClock(Now);
        var positions = new CountingPositionSource();
        var held = new HeldCloseNotificationTracker();
        var guard = new ProtectiveStopGuard(
            broker, positions, stops, store, reservations, clock, heldCloseNotifications: held,
            closeRejections: new CloseRejectionTracker());
        clock.UtcNow = Now + HeldCloseNotificationTracker.RenotifyInterval;

        var patrol = await guard.RunOnceAsync(batchSize: 100);

        patrol.Scanned.Should().Be(0);
        patrol.Events.Should().BeEmpty("ガードはこの建玉を知らない＝1 時間ごとの再通知は起きない");
        positions.Calls.Should().Be(0, "巡回対象が無ければ建玉の照会もしない");

        // 同じ承認の再配送（再起動後の再処理を含む）。
        var redelivered = await service.ExecuteAsync(approved);

        redelivered.CoverageLost.Should().BeNull("相 1 で既存結果を返し、保護喪失は出し直さない");
        broker.MarketCloseCount.Should().Be(1, "成行も重ねない");
    }

    [Fact]
    public async Task 成行手仕舞いが成功したら手仕舞いレグの予約を確定する()
    {
        var broker = new ScriptedBroker { EntryStatus = OrderStatus.Filled, EntryFilled = 10, Stop = StopBehavior.Reject };
        var (service, store, _, reservations) = NewService(broker);
        var approved = Approved(Intent());
        var closeDecisionId = ProtectiveStopIds.CloseDecisionId(approved.DecisionId, attempt: 1);

        var result = await service.ExecuteAsync(approved);

        result.CoverageLost!.Remediation.Should().Be(ProtectiveStopRemediation.PositionClosed);
        reservations.Find(closeDecisionId)!.State
            .Should().Be(OrderExecutionService.Features.OrderExecution.OrderDispatchState.Completed);
        store.FindByDecisionId(closeDecisionId)!.OrderId.Should().Be("close-1");
    }

    // 🔴 T-10-639, FR-10, FR-11, UC-06, #857, IADR-0369 決定1:
    // **確認できた拒否を「手仕舞い済み」と扱わない（発注側）。**
    // 是正前は `closeOrder` が非 null でありさえすれば PositionClosed を返しており、
    //   - 通知が「建玉を成行で手仕舞いました」と**事実の逆**を言い、
    //   - 送られてもいない決済の承認行が取引台帳へ足されて 30 分の窓のあいだ在庫を押さえていた。
    [Fact]
    public async Task 成行手仕舞いが確認できた拒否なら_手仕舞い済みを主張せず台帳へレグを運ばない_否定形()
    {
        var broker = new ScriptedBroker
        {
            EntryStatus = OrderStatus.Filled,
            EntryFilled = 10,
            Stop = StopBehavior.Reject,
            MarketClose = RemedyBehavior.Reject,
        };
        var (service, store, _, reservations) = NewService(broker);
        var approved = Approved(Intent());
        var closeDecisionId = ProtectiveStopIds.CloseDecisionId(approved.DecisionId, attempt: 1);

        var result = await service.ExecuteAsync(approved);

        broker.MarketCloseCount.Should().Be(1);
        var lost = result.CoverageLost!;
        lost.Remediation.Should().Be(ProtectiveStopRemediation.CloseRejected,
            "確認できた拒否は PositionClosed（手仕舞い済み）でも CloseDispatchIndeterminate（不明）でもない");
        lost.Cause.Should().Be(ProtectiveStopLossCause.RejectedAtEntry);
        lost.Quantity.Should().Be(10);
        // 🔴 手仕舞いレグは運ばない＝取引台帳に承認行が足されない（送った成行は生きていない）。
        lost.CloseIntent.Should().BeNull("生きていない成行を処理中の決済として在庫から引かせない");
        lost.CloseDecisionId.Should().Be(closeDecisionId, "拒否された発注記録との相関のために ID だけは残す");

        // 拒否は**不明ではない**ので、予約は据え置かず確定する（記録も残る＝一次証跡）。
        reservations.Find(closeDecisionId)!.State
            .Should().Be(OrderExecutionService.Features.OrderExecution.OrderDispatchState.Completed);
        store.FindByDecisionId(closeDecisionId)!.Status.Should().Be(OrderStatus.Rejected);
    }

    [Theory]
    [InlineData(StopBehavior.Throw)]
    [InlineData(StopBehavior.Unavailable)]
    public async Task 逆指値の発注例外も未受理と同じ分岐に入る(StopBehavior stop)
    {
        var broker = new ScriptedBroker { EntryStatus = OrderStatus.Filled, EntryFilled = 10, Stop = stop };
        var (service, _, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(Intent()));

        result.CoverageLost.Should().NotBeNull("逆指値を張れなかった建玉は持たない（fail-closed）");
        result.CoverageLost!.Remediation.Should().Be(ProtectiveStopRemediation.PositionClosed);
    }

    [Fact]
    public async Task エントリーが終端失敗なら保護レグを試みない()
    {
        var broker = new ScriptedBroker { EntryStatus = OrderStatus.Rejected, EntryFilled = 0 };
        var (service, _, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(Intent()));

        broker.StopPlaceCount.Should().Be(0, "建玉が生じないため保護対象が無い");
        result.StopPlaced.Should().BeNull();
        result.CoverageLost.Should().BeNull();
    }

    // ---- 見送り（逆指値を張れない Open は建玉を作らない・IADR-0210 決定1 / IADR-0211） ----

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task StopLossPriceの無いOpenは発注せず見送る(int? stopLoss)
    {
        var broker = new ScriptedBroker();
        var (service, store, _, reservations) = NewService(broker);
        var approved = Approved(Intent(stopLoss: stopLoss));

        var result = await service.ExecuteAsync(approved);

        var forgone = result.Forgone!;
        forgone.Reason.Should().Be(OrderDispatchForgoneReason.StopLossPriceMissing);
        result.Executed.Should().BeNull();
        store.GetAll().Should().BeEmpty("発注していない");
        reservations.Find(approved.DecisionId)!.State.Should().Be(
            OrderDispatchState.Forgone, "発注に着手していないため予約は無く、見送りの記録だけが残る（#876）");
    }

    [Fact]
    public async Task 逆指値能力の無いブローカへのOpenは発注せず見送る()
    {
        // IProtectiveOrderBroker 非実装＝逆指値を張れない → 建玉を作らない側へ倒す（fail-closed）。
        var order = new BrokerOrder("o1", Intent(), OrderStatus.Filled, 10, 1_000m, Now, Now);
        var broker = new NonProtectiveBroker(order);
        var (service, store, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(Intent()));

        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.StopOrderUnsupported);
        broker.PlaceCount.Should().Be(0, "エントリー自体を発注しない");
        store.GetAll().Should().BeEmpty();
    }

    private sealed class NonProtectiveBroker(BrokerOrder order) : IBrokerAdapter
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;
        public int PlaceCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            PlaceCount++;
            return Task.FromResult(order);
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(order);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ---- 否定形: 決済（Close）には逆指値を張らない ----

    [Fact]
    public async Task Close注文には保護逆指値を張らない_否定形()
    {
        // 決済に逆指値を重ねると、決済後に残った逆指値が反対方向の建玉を生む（二重決済問題の裏面）。
        var broker = new ScriptedBroker { EntryStatus = OrderStatus.Filled, EntryFilled = 10 };
        var (service, _, _, _) = NewService(broker);
        var closeIntent = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 10, 1_000m, PositionEffect.Close);

        var result = await service.ExecuteAsync(new OrderApproved(Guid.NewGuid(), closeIntent, 10, Now));

        broker.StopPlaceCount.Should().Be(0);
        result.StopPlaced.Should().BeNull();
        result.Executed.Should().NotBeNull("Close は StopLossPrice なしで従来どおり執行される");
    }

    // ---- プロパティベース: 不変条件「建玉あり ⇒ 有効な逆指値あり（または人手対応の Critical）」----

    [Fact]
    public async Task 不変条件_建玉が残るなら有効な逆指値があるか人手対応が発火している()
    {
        // 疑似乱数（シード固定・再現可能）でエントリー約定・逆指値・取消・手仕舞いの挙動を振り、
        // どの組み合わせでも「逆指値なしの建玉が黙って残る」状態にならないことを検証する（issue #331 受け入れ基準）。
        var random = new Random(20260828);
        var entryStatuses = new[] { OrderStatus.Accepted, OrderStatus.PartiallyFilled, OrderStatus.Filled, OrderStatus.Rejected };

        for (var i = 0; i < 500; i++)
        {
            var entryStatus = entryStatuses[random.Next(entryStatuses.Length)];
            var filled = entryStatus switch
            {
                OrderStatus.Filled => 10,
                OrderStatus.PartiallyFilled => random.Next(1, 10),
                _ => 0,
            };
            var broker = new ScriptedBroker
            {
                EntryStatus = entryStatus,
                EntryFilled = filled,
                Stop = (StopBehavior)random.Next(4),
                Cancel = (RemedyBehavior)random.Next(2),
                MarketClose = (RemedyBehavior)random.Next(2),
                RequeryFilled = random.Next(3) switch { 0 => null, 1 => 0, _ => random.Next(1, 10) },
            };
            var (service, _, stops, _) = NewService(broker);
            var approved = Approved(Intent());

            var result = await service.ExecuteAsync(approved);

            // 建玉が残り得る = エントリーが終端失敗でなく、取消/手仕舞いで解消されていない。
            var positionMayRemain = entryStatus != OrderStatus.Rejected
                && result.CoverageLost?.Remediation is not (ProtectiveStopRemediation.EntryCancelled
                    or ProtectiveStopRemediation.PositionClosed);

            if (positionMayRemain)
            {
                var protectedByStop = result.StopPlaced is not null
                    && stops.Find(approved.DecisionId)?.State == ProtectiveStopState.Active;
                // #848, IADR-0117（改定 7）: 成行手仕舞いの結果が未確認（CloseDispatchIndeterminate）も Critical の
                // 人手対応である（通知の重大度は NotificationFormatterTests が固定する）。
                // #857, IADR-0369: **確認できた拒否**（CloseRejected）も同じく Critical の人手対応である
                //（黙って「手仕舞い済み」にしない、が本 issue の中心）。
                var humanAlerted = result.CoverageLost?.Remediation
                    is ProtectiveStopRemediation.None or ProtectiveStopRemediation.CloseDispatchIndeterminate
                    or ProtectiveStopRemediation.CloseRejected;

                (protectedByStop || humanAlerted).Should().BeTrue(
                    $"逆指値なしの建玉が黙って残ってはならない（case {i}: entry={entryStatus}/{filled}"
                    + $" stop={broker.Stop} cancel={broker.Cancel} close={broker.MarketClose}）");
            }
        }
    }
}
