using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Infrastructure.Persistence;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// 🔴 FR-10, FR-05, NFR-09, ADR-0045 決定1, #856, IADR-0488（T-10-2230〜T-10-2238）:
// 送信結果を確認できない発注を SIMULATE で意図的に作る故障注入。利用者裁定 2026-10-03。
//   - AfterSend: 送信した後で不明にする → 突合は「発注済み」。
//   - BeforeSend: 送信せずに不明にする → 突合は「未発注」（門が閉じているので据え置き）。**誤判定は二重発注に直結する本丸。**
// 注入は既存のアダプタの catch を通って BrokerDispatchIndeterminateException になり、予約・突合のコードは変えない。
public class IndeterminateDispatchFaultInjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 14, 0, 0, TimeSpan.Zero);

    // ---- 試験の部品 ----

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    // OpenD の代わり。送った注文を remark ごとに覚え、remark 突合（FindOrderByClientIdAsync）は覚えた注文だけを返す
    // ——「全列挙が成功して一致ゼロ」は null（＝確実に未発注）。本物のプローブがその契約で NotPlaced を返す。
    private sealed class RecordingOpenD : IMoomooTradeClient
    {
        private readonly List<(MoomooOrderRequest Request, string OrderId)> _placed = [];

        public IReadOnlyList<MoomooOrderRequest> Placed => _placed.Select(p => p.Request).ToList();

        public Task<MoomooOrderResult> PlaceOrderAsync(MoomooOrderRequest request, CancellationToken cancellationToken = default)
        {
            var orderId = (9049618348733212700L + _placed.Count).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _placed.Add((request, orderId));
            return Task.FromResult(new MoomooOrderResult(orderId, MoomooOrderState.Submitted, 0, 0m));
        }

        public Task<MoomooOrderResult?> QueryOrderAsync(string orderId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MoomooOrderResult?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<MoomooOrderSnapshot?> FindOrderByClientIdAsync(
            string clientOrderId, DateTimeOffset reservedAtUtc, CancellationToken cancellationToken = default)
        {
            var hit = _placed.FirstOrDefault(p => p.Request.Remark == clientOrderId);
            return Task.FromResult(hit.Request is null
                ? null
                : new MoomooOrderSnapshot(hit.OrderId, MoomooOrderState.Submitted, hit.Request.Symbol, hit.Request.Market,
                    hit.Request.Side, hit.Request.Quantity, hit.Request.Price, 0, 0m, Now, null));
        }

        public Task<IReadOnlyList<MoomooPositionSnapshot>> GetPositionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MoomooPositionSnapshot>>([]);

        public Task<MoomooAccountType?> GetAccountTypeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<MoomooAccountType?>(MoomooAccountType.Margin);

        public Task<decimal?> GetAccountEquityInBaseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<decimal?>(3_000m);
    }

    // 実際の送信が先に失敗する OpenD（AfterSend の対象でも、注入の前に実際の例外が出る）。
    private sealed class FailingOpenD : IMoomooTradeClient
    {
        public int Calls { get; private set; }

        public Task<MoomooOrderResult> PlaceOrderAsync(MoomooOrderRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new TimeoutException("OpenD の返信待ちがタイムアウトしました（試験）");
        }

        public Task<MoomooOrderResult?> QueryOrderAsync(string orderId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MoomooOrderResult?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<MoomooOrderSnapshot?> FindOrderByClientIdAsync(
            string clientOrderId, DateTimeOffset reservedAtUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<MoomooOrderSnapshot?>(null);

        public Task<IReadOnlyList<MoomooPositionSnapshot>> GetPositionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MoomooPositionSnapshot>>([]);

        public Task<MoomooAccountType?> GetAccountTypeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<MoomooAccountType?>(MoomooAccountType.Margin);

        public Task<decimal?> GetAccountEquityInBaseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<decimal?>(3_000m);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _records = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Records
        {
            get { lock (_records) return _records.ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_records) _records.Add((logLevel, formatter(state, exception)));
        }
    }

    private static IndeterminateDispatchFaultInjectionOptions Options(
        IndeterminateDispatchFaultMode mode, string symbols = "AAPL", DateTimeOffset? expiresAt = null) =>
        new(mode, symbols.Split(',', StringSplitOptions.TrimEntries), expiresAt ?? Now.AddHours(6));

    private static IndeterminateDispatchFaultInjectingClient Injecting(
        IMoomooTradeClient inner,
        IndeterminateDispatchFaultInjectionOptions options,
        TimeProvider? time = null,
        ILogger<IndeterminateDispatchFaultInjectingClient>? logger = null) =>
        new(inner, options, logger, time ?? new MutableTimeProvider(Now));

    private static MoomooOrderRequest Request(
        string symbol = "AAPL",
        PositionEffect? effect = PositionEffect.Open,
        MoomooOrderKind kind = MoomooOrderKind.Limit,
        string? remark = "0123456789abcdef0123456789abcdef") =>
        new(symbol, MoomooMarket.UnitedStates, MoomooSide.Buy, 1, 100m, remark, kind,
            TriggerPrice: kind is MoomooOrderKind.Stop or MoomooOrderKind.StopLimit ? 95m : null,
            TrailValue: kind == MoomooOrderKind.TrailingStop ? 5m : null,
            PositionEffect: effect);

    private static OrderIntent EntryIntent(string symbol = "AAPL") =>
        new(symbol, Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
            Quantity: 1, Price: 100m, PositionEffect.Open, StopLossPrice: 95m);

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    // ---- T-10-2230: 既定は無効 ----

    // T-10-2230: 未設定・空・None はすべて無効で、他のキーは読まない（None に余計な値があっても起動を止めない）。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("None")]
    [InlineData(" none ")]
    public void T_10_2230_既定と明示のNoneは無効で他のキーを読まない(string? mode)
    {
        var options = IndeterminateDispatchFaultInjectionOptions.FromConfiguration(Config(
            (IndeterminateDispatchFaultInjectionOptions.ModeKey, mode),
            (IndeterminateDispatchFaultInjectionOptions.SymbolsKey, "AAPL,*"),
            (IndeterminateDispatchFaultInjectionOptions.ExpiresAtKey, "読めない")), Now);

        options.Enabled.Should().BeFalse();
        options.Should().BeSameAs(IndeterminateDispatchFaultInjectionOptions.Disabled);
        var act = () => options.EnsureAllowed(
            BrokerSelection.Parse("paper", null), liveTradingReleased: true, configuredTrdEnv: "real", realAccountQueryEnabled: true);
        act.Should().NotThrow("無効なら構成の組み合わせを問わない（現行の構成を 1 つも止めない）");
    }

    // T-10-2230: 無効の構成をデコレータへ渡しても（本番は包まない）、発注は 1 本も変わらない。
    [Fact]
    public async Task T_10_2230_無効なら新規建ても素通しする()
    {
        var openD = new RecordingOpenD();
        var client = Injecting(openD, IndeterminateDispatchFaultInjectionOptions.Disabled);

        var result = await client.PlaceOrderAsync(Request(), TestContext.Current.CancellationToken);

        result.OrderId.Should().NotBeNullOrEmpty();
        openD.Placed.Should().ContainSingle();
        client.HasFired.Should().BeFalse();
    }

    // ---- T-10-2231 / T-10-2232: 2 つの形 ----

    // T-10-2231: AfterSend は実際に送信してから注入の例外を投げる。
    [Fact]
    public async Task T_10_2231_AfterSendは送信した後で注入の例外を投げる()
    {
        var openD = new RecordingOpenD();
        var client = Injecting(openD, Options(IndeterminateDispatchFaultMode.AfterSend));

        var act = async () => await client.PlaceOrderAsync(Request(), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<IndeterminateDispatchFaultInjectedException>())
            .Which.Message.Should().Contain("AfterSend").And.Contain("送信済み");
        openD.Placed.Should().ContainSingle("証券会社には注文が 1 本ある（突合は発注済みと答えるはず）");
        client.HasFired.Should().BeTrue();
    }

    // T-10-2232: BeforeSend は送信せずに注入の例外を投げる。
    [Fact]
    public async Task T_10_2232_BeforeSendは送信せずに注入の例外を投げる()
    {
        var openD = new RecordingOpenD();
        var client = Injecting(openD, Options(IndeterminateDispatchFaultMode.BeforeSend));

        var act = async () => await client.PlaceOrderAsync(Request(), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<IndeterminateDispatchFaultInjectedException>())
            .Which.Message.Should().Contain("BeforeSend").And.Contain("送信していません");
        openD.Placed.Should().BeEmpty("証券会社には注文が無い（突合は未発注と答えるはず）");
        client.HasFired.Should().BeTrue();
    }

    // T-10-2231 / T-10-2232: 本物のアダプタは注入を「届いたか不明」（BrokerDispatchIndeterminateException）に包む
    // ——拒否（Rejected）にも、接続確立の失敗（見送り）にもならない。
    [Theory]
    [InlineData(IndeterminateDispatchFaultMode.AfterSend, 1)]
    [InlineData(IndeterminateDispatchFaultMode.BeforeSend, 0)]
    public async Task T_10_2231_2232_本物のアダプタ越しに届いたか不明になる(IndeterminateDispatchFaultMode mode, int sent)
    {
        var openD = new RecordingOpenD();
        var adapter = new MoomooBrokerAdapter(Injecting(openD, Options(mode)), BrokerProvider.MoomooSimulate);

        var act = async () => await adapter.PlaceOrderAsync(EntryIntent(), Guid.NewGuid(), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<BrokerDispatchIndeterminateException>())
            .Which.InnerException.Should().BeOfType<IndeterminateDispatchFaultInjectedException>();
        openD.Placed.Should().HaveCount(sent);
    }

    // T-10-2231 / T-10-2232: 発注執行（承認の配送）では、予約は Reserved のまま・結果は保存しない（既存の経路そのもの）。
    [Theory]
    [InlineData(IndeterminateDispatchFaultMode.AfterSend, 1)]
    [InlineData(IndeterminateDispatchFaultMode.BeforeSend, 0)]
    public async Task T_10_2231_2232_発注執行は予約をReservedのまま据え置く(IndeterminateDispatchFaultMode mode, int sent)
    {
        var openD = new RecordingOpenD();
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var adapter = new MoomooBrokerAdapter(Injecting(openD, Options(mode)), BrokerProvider.MoomooSimulate);
        var approved = new OrderApproved(Guid.NewGuid(), EntryIntent(), 1, Now);

        var act = async () => await new AppSvc(adapter, store, reservations, new FixedClock(Now)).ExecuteAsync(approved);

        await act.Should().ThrowAsync<BrokerDispatchIndeterminateException>();
        openD.Placed.Should().HaveCount(sent);
        store.GetAll().Should().BeEmpty("結果を知らないまま記録を作らない（拒否にも見送りにも畳まない）");
        var reservation = reservations.Find(approved.DecisionId);
        reservation.Should().NotBeNull();
        reservation!.State.Should().Be(OrderDispatchState.Reserved);
        reservation.BrokerProvider.Should().Be(BrokerProvider.MoomooSimulate, "SIMULATE の門で判定される");
    }

    // ---- T-10-2233: 突合まで通す（PoC が実機で観測することの写し） ----

    // 🔴 T-10-2233: 本物のアダプタ・本物のプローブ（remark 突合）・本物の突合を通すと、
    // AfterSend は「発注済み」（終端化・証券会社の注文 ID）、BeforeSend は「未発注」で据え置き（門が閉）になる。
    [Theory]
    [InlineData(IndeterminateDispatchFaultMode.AfterSend)]
    [InlineData(IndeterminateDispatchFaultMode.BeforeSend)]
    public async Task T_10_2233_突合はAfterSendを発注済みBeforeSendを未発注と判定する(IndeterminateDispatchFaultMode mode)
    {
        var openD = new RecordingOpenD();
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var adapter = new MoomooBrokerAdapter(Injecting(openD, Options(mode)), BrokerProvider.MoomooSimulate);
        var clock = new FixedClock(Now);
        var approved = new OrderApproved(Guid.NewGuid(), EntryIntent(), 1, Now);
        var dispatch = async () => await new AppSvc(adapter, store, reservations, clock).ExecuteAsync(approved);
        await dispatch.Should().ThrowAsync<BrokerDispatchIndeterminateException>();

        // 突合は素の OpenD クライアントで照会する（Program.cs と同じ。プローブは包まない）。
        var result = await new OrderReservationReconciler(
                reservations, store, new MoomooReservationBrokerProbe(openD), adapter, clock)
            .ReconcileAsync(Now.AddHours(3), batchSize: 10, cancellationToken: TestContext.Current.CancellationToken);

        result.Scanned.Should().Be(1);
        if (mode == IndeterminateDispatchFaultMode.AfterSend)
        {
            result.Terminalized.Should().Be(1);
            result.Executed.Should().ContainSingle().Which.OrderId.Should().Be("9049618348733212700",
                "証券会社が採番した注文 ID で確定する");
            reservations.Find(approved.DecisionId)!.State.Should().Be(OrderDispatchState.Completed);
            result.HeldNotPlaced.Should().BeEmpty();
        }
        else
        {
            result.HeldNotPlaced.Should().ContainSingle().Which.Should().Be(approved.DecisionId);
            result.Released.Should().Be(0, "解放の門は閉じている（本件は門を開けない）");
            reservations.Find(approved.DecisionId)!.State.Should().Be(OrderDispatchState.Reserved);
            openD.Placed.Should().BeEmpty();
            store.GetAll().Should().BeEmpty();
        }
    }

    // ---- T-10-2234: 範囲の制限 ----

    // T-10-2234: 許可外の銘柄は素通しで、1 回分を消費しない（後から来た許可銘柄に当たる）。
    [Fact]
    public async Task T_10_2234_許可外の銘柄は素通しで1回分を消費しない()
    {
        var openD = new RecordingOpenD();
        var client = Injecting(openD, Options(IndeterminateDispatchFaultMode.BeforeSend, "aapl, MSFT"));
        var ct = TestContext.Current.CancellationToken;

        await client.PlaceOrderAsync(Request("NVDA"), ct);
        client.HasFired.Should().BeFalse();

        var act = async () => await client.PlaceOrderAsync(Request("msft"), ct);
        await act.Should().ThrowAsync<IndeterminateDispatchFaultInjectedException>("大小文字を問わず一致する");
        openD.Placed.Select(r => r.Symbol).Should().Equal("NVDA");
    }

    // T-10-2234: 1 プロセス 1 回。2 本目以降は素通し。
    [Theory]
    [InlineData(IndeterminateDispatchFaultMode.AfterSend)]
    [InlineData(IndeterminateDispatchFaultMode.BeforeSend)]
    public async Task T_10_2234_注入は1プロセス1回だけ(IndeterminateDispatchFaultMode mode)
    {
        var openD = new RecordingOpenD();
        var client = Injecting(openD, Options(mode));
        var ct = TestContext.Current.CancellationToken;

        var first = async () => await client.PlaceOrderAsync(Request(), ct);
        await first.Should().ThrowAsync<IndeterminateDispatchFaultInjectedException>();
        await client.PlaceOrderAsync(Request(), ct);
        await client.PlaceOrderAsync(Request(), ct);

        openD.Placed.Should().HaveCount(mode == IndeterminateDispatchFaultMode.AfterSend ? 3 : 2);
    }

    // T-10-2234: 同時に来ても 1 回だけ（1 回分は送る前に原子的に取る）。
    [Fact]
    public async Task T_10_2234_同時に来ても注入は1回だけ()
    {
        var client = Injecting(new RecordingOpenD(), Options(IndeterminateDispatchFaultMode.BeforeSend));

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 32).Select(async _ =>
        {
            await Task.Yield();
            try
            {
                await client.PlaceOrderAsync(Request(), TestContext.Current.CancellationToken);
                return false;
            }
            catch (IndeterminateDispatchFaultInjectedException)
            {
                return true;
            }
        }));

        outcomes.Count(injected => injected).Should().Be(1);
    }

    // T-10-2234: 期限を過ぎたら注入しない（期限ちょうども注入しない）。
    [Fact]
    public async Task T_10_2234_期限を過ぎたら注入しない()
    {
        var openD = new RecordingOpenD();
        var time = new MutableTimeProvider(Now);
        var client = Injecting(openD, Options(IndeterminateDispatchFaultMode.BeforeSend, expiresAt: Now.AddHours(1)), time);
        time.Now = Now.AddHours(1);

        await client.PlaceOrderAsync(Request(), TestContext.Current.CancellationToken);

        openD.Placed.Should().ContainSingle();
        client.HasFired.Should().BeFalse();
    }

    // T-10-2234: remark（DecisionId）の無い発注は突合できないので対象外。`*` は全銘柄に当たる。
    [Fact]
    public async Task T_10_2234_remarkの無い発注は対象外で星印は全銘柄に当たる()
    {
        var openD = new RecordingOpenD();
        var client = Injecting(openD, Options(IndeterminateDispatchFaultMode.BeforeSend, "*"));
        var ct = TestContext.Current.CancellationToken;

        await client.PlaceOrderAsync(Request("NVDA", remark: null), ct);
        await client.PlaceOrderAsync(Request("NVDA", remark: ""), ct);
        client.HasFired.Should().BeFalse();

        var act = async () => await client.PlaceOrderAsync(Request("NVDA"), ct);
        await act.Should().ThrowAsync<IndeterminateDispatchFaultInjectedException>();
    }

    // T-10-2234: AfterSend の対象で実際の送信が先に失敗したら、実際の例外をそのまま伝播させ、1 回分は使い切る。
    [Fact]
    public async Task T_10_2234_実際の送信が先に失敗したら注入せず1回分を使い切る()
    {
        var openD = new FailingOpenD();
        var client = Injecting(openD, Options(IndeterminateDispatchFaultMode.AfterSend));

        var act = async () => await client.PlaceOrderAsync(Request(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<TimeoutException>();
        client.HasFired.Should().BeTrue("戻すと『実は届いた送信』の後に 2 本目を注入し得る");
        var again = async () => await client.PlaceOrderAsync(Request(), TestContext.Current.CancellationToken);
        await again.Should().ThrowAsync<TimeoutException>();
        openD.Calls.Should().Be(2);
    }

    // ---- T-10-2235: 保護レグ・手仕舞いには当てない ----

    // T-10-2235: 新規建ての指値以外（手仕舞いの全種別・効果が不明・新規建ての成行）は素通しで、1 回分を消費しない。
    [Theory]
    [InlineData(PositionEffect.Close, MoomooOrderKind.Limit)]
    [InlineData(PositionEffect.Close, MoomooOrderKind.Market)]
    [InlineData(PositionEffect.Close, MoomooOrderKind.Stop)]
    [InlineData(PositionEffect.Close, MoomooOrderKind.StopLimit)]
    [InlineData(PositionEffect.Close, MoomooOrderKind.TrailingStop)]
    [InlineData(null, MoomooOrderKind.Limit)]
    [InlineData(PositionEffect.Open, MoomooOrderKind.Market)]
    [InlineData(PositionEffect.Open, MoomooOrderKind.Stop)]
    public async Task T_10_2235_新規建ての指値以外には当てない(PositionEffect? effect, MoomooOrderKind kind)
    {
        var openD = new RecordingOpenD();
        var client = Injecting(openD, Options(IndeterminateDispatchFaultMode.BeforeSend, "*"));

        await client.PlaceOrderAsync(Request(effect: effect, kind: kind), TestContext.Current.CancellationToken);

        openD.Placed.Should().ContainSingle();
        client.HasFired.Should().BeFalse();
    }

    // T-10-2235: 本物のアダプタの保護レグの口（S0 の逆指値・成行の手仕舞い・S3 の代替注文種別）と指値の手仕舞いは、
    // 注入が有効でも送信され、届いたか不明にならない。
    [Fact]
    public async Task T_10_2235_本物のアダプタの保護レグと手仕舞いには当てない()
    {
        var openD = new RecordingOpenD();
        var client = Injecting(openD, Options(IndeterminateDispatchFaultMode.BeforeSend, "*"));
        var adapter = new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate);
        var close = EntryIntent() with { Side = TradeSide.Sell, PositionEffect = PositionEffect.Close, StopLossPrice = null };
        var ct = TestContext.Current.CancellationToken;

        await adapter.PlaceStopOrderAsync(close, 95m, Guid.NewGuid(), ct);
        await adapter.PlaceMarketOrderAsync(close, Guid.NewGuid(), ct);
        await adapter.PlaceAlternativeStopOrderAsync(close, 95m, 100m, Guid.NewGuid(), ct);
        await adapter.PlaceOrderAsync(close, Guid.NewGuid(), ct);

        openD.Placed.Should().HaveCount(4);
        openD.Placed.Should().OnlyContain(r => r.PositionEffect == PositionEffect.Close, "アダプタは発注意図の効果を載せる");
        client.HasFired.Should().BeFalse();
    }

    // ---- T-10-2236: 起動時の拒否（SIMULATE 限定） ----

    // T-10-2236: moomoo SIMULATE で、実弾が未解禁・TrdEnv が simulate・実弾口座の照会が無効なら受理する。
    [Theory]
    [InlineData(null)]
    [InlineData("simulate")]
    [InlineData(" SIMULATE ")]
    public void T_10_2236_SIMULATEの構成なら受理する(string? trdEnv)
    {
        var act = () => Options(IndeterminateDispatchFaultMode.BeforeSend).EnsureAllowed(
            BrokerSelection.Parse("moomoo", "sim"), liveTradingReleased: false, trdEnv, realAccountQueryEnabled: false);

        act.Should().NotThrow();
    }

    // 🔴 T-10-2236: 内蔵 paper・live 階層・実弾解禁・TrdEnv が simulate 以外・実弾口座の照会が有効なら起動を止める。
    [Theory]
    [InlineData("paper", "sim", false, null, false, "paper")]
    [InlineData("moomoo", "live", false, null, false, "moomoo-live")]
    [InlineData("moomoo", "sim", true, null, false, "LiveTradingReleased")]
    [InlineData("moomoo", "sim", false, "real", false, "TrdEnv")]
    [InlineData("moomoo", "sim", false, null, true, "実弾口座の読み取り専用の照会")]
    public void T_10_2236_SIMULATE以外の構成では起動を止める(
        string provider, string environment, bool liveReleased, string? trdEnv, bool realQuery, string expected)
    {
        foreach (var mode in new[] { IndeterminateDispatchFaultMode.AfterSend, IndeterminateDispatchFaultMode.BeforeSend })
        {
            var act = () => Options(mode).EnsureAllowed(
                new BrokerSelection(
                    provider == "paper" ? BrokerVendor.Paper : BrokerVendor.Moomoo,
                    environment == "live" ? BrokerEnvironment.Live : BrokerEnvironment.Simulated),
                liveReleased, trdEnv, realQuery);

            act.Should().Throw<InvalidOperationException>()
                .WithMessage($"*{IndeterminateDispatchFaultInjectionOptions.ModeKey}*")
                .Which.Message.Should().Contain(expected).And.Contain("SIMULATE 限定");
        }
    }

    // ---- T-10-2237: 不正な値は起動を止める ----

    // T-10-2237: 未知の形・銘柄なし・星印の混在・期限なし・読めない期限・24 時間より先の期限は起動時に止める。
    [Theory]
    [InlineData("Sometimes", "AAPL", "2026-10-05T20:00:00Z", "Mode")]
    [InlineData("true", "AAPL", "2026-10-05T20:00:00Z", "Mode")]
    [InlineData("AfterSend", null, "2026-10-05T20:00:00Z", "Symbols")]
    [InlineData("AfterSend", " , ", "2026-10-05T20:00:00Z", "Symbols")]
    [InlineData("BeforeSend", "AAPL,*", "2026-10-05T20:00:00Z", "Symbols")]
    [InlineData("BeforeSend", "AAPL", null, "ExpiresAtUtc")]
    [InlineData("BeforeSend", "AAPL", "来週", "ExpiresAtUtc")]
    [InlineData("BeforeSend", "AAPL", "2026-10-06T14:00:01Z", "ExpiresAtUtc")]
    public void T_10_2237_不正な値は起動時に止める(string mode, string? symbols, string? expiresAt, string key)
    {
        var act = () => IndeterminateDispatchFaultInjectionOptions.FromConfiguration(Config(
            (IndeterminateDispatchFaultInjectionOptions.ModeKey, mode),
            (IndeterminateDispatchFaultInjectionOptions.SymbolsKey, symbols),
            (IndeterminateDispatchFaultInjectionOptions.ExpiresAtKey, expiresAt)), Now);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*FaultInjection:IndeterminateDispatch:{key}*");
    }

    // T-10-2237: 正しい値を読む（大小文字・空白・重複を問わない。期限ちょうど 24 時間は受理）。
    [Fact]
    public void T_10_2237_正しい値を読む()
    {
        var options = IndeterminateDispatchFaultInjectionOptions.FromConfiguration(Config(
            (IndeterminateDispatchFaultInjectionOptions.ModeKey, " beforesend "),
            (IndeterminateDispatchFaultInjectionOptions.SymbolsKey, "AAPL, msft ,aapl"),
            (IndeterminateDispatchFaultInjectionOptions.ExpiresAtKey, "2026-10-06T23:00:00+09:00")), Now);

        options.Mode.Should().Be(IndeterminateDispatchFaultMode.BeforeSend);
        options.Symbols.Should().Equal("AAPL", "msft");
        options.ExpiresAt.Should().Be(Now.AddHours(24));
    }

    // T-10-2237: 期限を過ぎた構成は起動を止めない（注入しないだけ）。構成の Warning に「期限切れ」と出る。
    [Fact]
    public async Task T_10_2237_期限切れの構成は起動を止めず注入しない()
    {
        var options = IndeterminateDispatchFaultInjectionOptions.FromConfiguration(Config(
            (IndeterminateDispatchFaultInjectionOptions.ModeKey, "BeforeSend"),
            (IndeterminateDispatchFaultInjectionOptions.SymbolsKey, "AAPL"),
            (IndeterminateDispatchFaultInjectionOptions.ExpiresAtKey, "2026-10-04T00:00:00Z")), Now);
        var logger = new RecordingLogger<IndeterminateDispatchFaultInjectingClient>();
        var openD = new RecordingOpenD();
        var client = Injecting(openD, options, logger: logger);

        await client.PlaceOrderAsync(Request(), TestContext.Current.CancellationToken);

        openD.Placed.Should().ContainSingle();
        logger.Records.Should().ContainSingle().Which.Message.Should().Contain("期限切れのため注入しません");
    }

    // ---- T-10-2238: ログ ----

    // T-10-2238: 注入の Warning は発火 1 回につき 1 行で、形・DecisionId・銘柄・（AfterSend なら）注文 ID を載せる。
    [Theory]
    [InlineData(IndeterminateDispatchFaultMode.AfterSend, "9049618348733212700")]
    [InlineData(IndeterminateDispatchFaultMode.BeforeSend, "（送信していない）")]
    public async Task T_10_2238_注入のWarningは1回につき1行(IndeterminateDispatchFaultMode mode, string orderId)
    {
        var logger = new RecordingLogger<IndeterminateDispatchFaultInjectingClient>();
        var client = Injecting(new RecordingOpenD(), Options(mode), logger: logger);
        var ct = TestContext.Current.CancellationToken;

        var act = async () => await client.PlaceOrderAsync(Request(), ct);
        await act.Should().ThrowAsync<IndeterminateDispatchFaultInjectedException>();
        await client.PlaceOrderAsync(Request(), ct);

        var fired = logger.Records.Where(r => r.Message.StartsWith("故障注入: ", StringComparison.Ordinal)).ToList();
        fired.Should().ContainSingle();
        fired[0].Level.Should().Be(LogLevel.Warning);
        fired[0].Message.Should().Contain($"形={mode}").And.Contain("DecisionId=0123456789abcdef0123456789abcdef")
            .And.Contain("銘柄=AAPL").And.Contain($"注文ID={orderId}");
        logger.Records.Should().HaveCount(2, "構成の 1 行と発火の 1 行だけ");
        logger.Records[0].Level.Should().Be(LogLevel.Warning);
        logger.Records[0].Message.Should().Contain("故障注入（送信結果を確認できない発注）が構成されています").And.Contain($"形={mode}");
    }

    // T-10-2238: 外部由来の文字列（銘柄・remark）は無害化してからログへ落とす（CWE-117）。
    [Fact]
    public async Task T_10_2238_外部由来の文字列は無害化してからログへ落とす()
    {
        var logger = new RecordingLogger<IndeterminateDispatchFaultInjectingClient>();
        var client = Injecting(new RecordingOpenD(), Options(IndeterminateDispatchFaultMode.BeforeSend, "*"), logger: logger);

        var act = async () => await client.PlaceOrderAsync(
            Request("AA\nPL", remark: "abc\r\n[INF] 偽の行"), TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<IndeterminateDispatchFaultInjectedException>();

        var fired = logger.Records.Single(r => r.Message.StartsWith("故障注入: ", StringComparison.Ordinal));
        fired.Message.Should().NotContain("\n").And.NotContain("\r").And.Contain("銘柄=AA_PL");
    }
}
