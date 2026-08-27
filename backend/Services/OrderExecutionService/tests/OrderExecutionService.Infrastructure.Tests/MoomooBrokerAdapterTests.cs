using AiStockTrading.OrderExecution.Infrastructure.Composable.Adapters;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.OrderExecution.Infrastructure.Tests;

// #13, FR-05, ADR-0002, IADR-0016: moomoo アダプタの写像・状態変換・SIMULATE 限定・fail-safe を fake client で検証する
// （実 OpenD 不使用）。実結合（MMApiMoomooTradeClient）は live 検証。
public class MoomooBrokerAdapterTests
{
    private sealed class FakeClient : IMoomooTradeClient
    {
        public MoomooOrderRequest? LastRequest { get; private set; }
        public MoomooOrderResult Result { get; set; } = new("mo-1", MoomooOrderState.FilledAll, 10, 100m);
        public Func<Exception>? ThrowOnPlace { get; set; }
        public MoomooOrderResult? QueryResult { get; set; }
        public string? CancelledId { get; private set; }

        public Task<MoomooOrderResult> PlaceOrderAsync(MoomooOrderRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            if (ThrowOnPlace is not null) throw ThrowOnPlace();
            return Task.FromResult(Result);
        }

        public Task<MoomooOrderResult?> QueryOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult(QueryResult);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            CancelledId = orderId;
            return Task.CompletedTask;
        }

        public Task<MoomooOrderSnapshot?> FindOrderByClientIdAsync(
            string clientOrderId, DateTimeOffset reservedAtUtc, CancellationToken ct = default) =>
            Task.FromResult<MoomooOrderSnapshot?>(null);

        // #292, IADR-0118: 建玉照会。既定は空列（建玉なし）。例外側は Throw で差し替える。
        public Func<Exception>? PositionsThrow { get; set; }

        public IReadOnlyList<MoomooPositionSnapshot> Positions { get; set; } = [];

        public Task<IReadOnlyList<MoomooPositionSnapshot>> GetPositionsAsync(CancellationToken ct = default)
        {
            if (PositionsThrow is not null) throw PositionsThrow();
            return Task.FromResult(Positions);
        }

        // #375, ADR-0021 決定3: 口座種別の照会。既定は信用口座（SIMULATE 口座の実測値）。
        // null＝種別不明、Throw＝照会失敗をそれぞれ差し替えて検証する。
        public Func<Exception>? AccountTypeThrow { get; set; }

        public MoomooAccountType? AccountType { get; set; } = MoomooAccountType.Margin;

        public Task<MoomooAccountType?> GetAccountTypeAsync(CancellationToken ct = default)
        {
            if (AccountTypeThrow is not null) throw AccountTypeThrow();
            return Task.FromResult(AccountType);
        }
    }

    // #141, IADR-0092: DecisionId を remark（client order id相当）として発注リクエストに載せることを検証する。
    [Fact]
    public async Task client_order_id_発注は_DecisionId_を_remark_に載せる()
    {
        var client = new FakeClient();
        var decisionId = Guid.NewGuid();

        await new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate).PlaceOrderAsync(Intent(), decisionId);

        client.LastRequest!.Remark.Should().Be(decisionId.ToString("N"));
    }

    [Fact]
    public async Task 通常発注は_remark_を付けない()
    {
        var client = new FakeClient();
        await new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate).PlaceOrderAsync(Intent());
        client.LastRequest!.Remark.Should().BeNull();
    }

    private static OrderIntent Intent(int qty = 10, decimal price = 100m, Market market = Market.UnitedStates,
        TradeSide side = TradeSide.Buy, BrokerProvider mode = BrokerProvider.InternalPaper) =>
        new("AAPL", market, side, ProductType.Cash, mode, qty, price);

    [Fact]
    public async Task 発注を_SIMULATE_リクエストへ写像し結果を_BrokerOrder_へ変換する()
    {
        var client = new FakeClient { Result = new("mo-9", MoomooOrderState.FilledAll, 10, 101m) };
        var order = await new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate).PlaceOrderAsync(
            Intent(qty: 10, price: 100m, market: Market.Japan, side: TradeSide.Sell));

        client.LastRequest.Should().Be(new MoomooOrderRequest("AAPL", MoomooMarket.Japan, MoomooSide.Sell, 10, 100m));
        order.OrderId.Should().Be("mo-9");
        order.Status.Should().Be(OrderStatus.Filled);
        order.FilledQuantity.Should().Be(10);
        order.AveragePrice.Should().Be(101m);
    }

    [Fact]
    public async Task Live_指定でも_SIMULATE_で発注する_実弾を撃たない()
    {
        // moomoo アダプタは Mode に依らず SIMULATE（client 実装が固定）で発注する。Live でも拒否せず送信する。
        var client = new FakeClient();
        var order = await new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate).PlaceOrderAsync(Intent(mode: BrokerProvider.MoomooReal));
        client.LastRequest.Should().NotBeNull();
        order.Status.Should().Be(OrderStatus.Filled);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(10, 0)]
    public async Task 不正注文_数量や価格が0以下_は送信せず_Rejected(int qty, decimal price)
    {
        var client = new FakeClient();
        var order = await new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate).PlaceOrderAsync(Intent(qty, price));
        order.Status.Should().Be(OrderStatus.Rejected);
        client.LastRequest.Should().BeNull(); // 送信していない
    }

    [Fact]
    public async Task client_例外_送信後の失敗_は_Rejected_に倒す_fail_safe()
    {
        // 送信後の分類不能な失敗（届いたか不明）は従来どおり終端 Rejected（予約とリコンサイルが守る）。
        var client = new FakeClient { ThrowOnPlace = () => new InvalidOperationException("応答異常") };
        var order = await new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate).PlaceOrderAsync(Intent());
        order.Status.Should().Be(OrderStatus.Rejected);
    }

    // FR-05, #331, IADR-0211: **接続確立の失敗（確実に未発注）は Rejected へ丸めない**——
    // 「拒否 = 証券会社が受理しなかった状態」の集計を接続障害で汚染しない（別状態・別集計の要）。
    [Fact]
    public async Task OpenD接続不可は_Rejected_に丸めず伝播する_否定形()
    {
        var client = new FakeClient
        {
            ThrowOnPlace = () => new Shared.Contracts.Ports.BrokerUnavailableException("OpenD 接続不可"),
        };
        var adapter = new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate);

        var act = async () => await adapter.PlaceOrderAsync(Intent());

        await act.Should().ThrowAsync<Shared.Contracts.Ports.BrokerUnavailableException>(
            "見送り（キューイングせず破棄）は呼び出し側が行う。Rejected にすると証券会社拒否の件数へ混入する");
    }

    // --- FR-10, #331, IADR-0210: 保護注文（IProtectiveOrderBroker） ---

    [Fact]
    public async Task 逆指値は_Stop種別とトリガー価格と_remark_で発注される()
    {
        var client = new FakeClient { Result = new("mo-stop", MoomooOrderState.Submitted, 0, 0m) };
        var adapter = new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate);
        var decisionId = Guid.NewGuid();
        var closeIntent = Intent(side: TradeSide.Sell) with { PositionEffect = PositionEffect.Close };

        var order = await adapter.PlaceStopOrderAsync(closeIntent, triggerPrice: 95m, decisionId);

        client.LastRequest!.Kind.Should().Be(MoomooOrderKind.Stop);
        client.LastRequest.TriggerPrice.Should().Be(95m);
        client.LastRequest.Remark.Should().Be(decisionId.ToString("N"), "レグも DecisionId で照合できるようにする");
        order.Status.Should().Be(OrderStatus.Accepted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task 発火価格が正でない逆指値は送信せず_Rejected(decimal trigger)
    {
        var client = new FakeClient();
        var adapter = new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate);

        var order = await adapter.PlaceStopOrderAsync(Intent(side: TradeSide.Sell), trigger, Guid.NewGuid());

        order.Status.Should().Be(OrderStatus.Rejected);
        client.LastRequest.Should().BeNull("送信していない（呼び出し側は建玉解消の分岐に入る）");
    }

    [Fact]
    public async Task 成行手仕舞いは_Market種別で発注される()
    {
        var client = new FakeClient { Result = new("mo-close", MoomooOrderState.FilledAll, 10, 99m) };
        var adapter = new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate);

        var order = await adapter.PlaceMarketOrderAsync(
            Intent(side: TradeSide.Sell) with { PositionEffect = PositionEffect.Close }, Guid.NewGuid());

        client.LastRequest!.Kind.Should().Be(MoomooOrderKind.Market);
        order.Status.Should().Be(OrderStatus.Filled);
    }

    [Fact]
    public void 注文状態を_OrderStatus_へ写像する()
    {
        MoomooBrokerAdapter.MapState(MoomooOrderState.Submitting).Should().Be(OrderStatus.Accepted);
        MoomooBrokerAdapter.MapState(MoomooOrderState.Submitted).Should().Be(OrderStatus.Accepted);
        MoomooBrokerAdapter.MapState(MoomooOrderState.Filling).Should().Be(OrderStatus.PartiallyFilled);
        MoomooBrokerAdapter.MapState(MoomooOrderState.FilledPart).Should().Be(OrderStatus.PartiallyFilled);
        MoomooBrokerAdapter.MapState(MoomooOrderState.FilledAll).Should().Be(OrderStatus.Filled);
        MoomooBrokerAdapter.MapState(MoomooOrderState.Cancelled).Should().Be(OrderStatus.Cancelled);
        MoomooBrokerAdapter.MapState(MoomooOrderState.Failed).Should().Be(OrderStatus.Rejected);
    }

    [Fact]
    public async Task 状態照会は_client_結果を返し_未知は_null()
    {
        var client = new FakeClient { QueryResult = new("mo-2", MoomooOrderState.Filling, 3, 99m) };
        var found = await new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate).GetOrderAsync("mo-2");
        found!.Status.Should().Be(OrderStatus.PartiallyFilled);
        found.FilledQuantity.Should().Be(3);

        client.QueryResult = null;
        (await new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate).GetOrderAsync("none")).Should().BeNull();
    }

    [Fact]
    public async Task 取消は_client_へ委譲する()
    {
        var client = new FakeClient();
        await new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate).CancelOrderAsync("mo-3");
        client.CancelledId.Should().Be("mo-3");
    }

    // --- #292, IADR-0118: 建玉照会（突合の入力） ---

    [Fact]
    public async Task 建玉を符号付きで共有契約へ写す()
    {
        var client = new FakeClient
        {
            Positions =
            [
                new MoomooPositionSnapshot("AAPL", MoomooMarket.UnitedStates, 4072, 20.5m),
                new MoomooPositionSnapshot("7203", MoomooMarket.Japan, -100, 2500m),
            ],
        };
        var adapter = new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate);

        var positions = await adapter.GetPositionsAsync();

        positions.Should().NotBeNull();
        positions!.Should().HaveCount(2);
        positions[0].Should().Be(new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 4072, 20.5m));
        positions[1].Should().Be(new BrokerPositionSnapshot("7203", Market.Japan, -100, 2500m));
    }

    [Fact]
    public async Task 建玉が無ければ空列を返す()
    {
        // 空列（建玉ゼロ）は観測事実。null（不明）と取り違えない。
        var positions = await new MoomooBrokerAdapter(new FakeClient(), BrokerProvider.MoomooSimulate).GetPositionsAsync();

        positions.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task 照会に失敗したら不明として_null_を返す()
    {
        // 空列に倒すと「ブローカは何も持っていない」と誤断定し、台帳の全建玉が乖離として報告される。
        var client = new FakeClient { PositionsThrow = () => new InvalidOperationException("OpenD 不達") };

        var positions = await new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate).GetPositionsAsync();

        positions.Should().BeNull();
    }

    // =====================================================================================
    // FR-19, FR-10, #375, ADR-0021 決定3, IADR-0153: 口座種別の照会（IBrokerAccountSource）
    // =====================================================================================

    // 引数は SDK の生値（int）で受ける。`MoomooAccountType` は internal であり、public なテストメソッドの
    // シグネチャに置けないためである（テスト対象の可視性を緩めない）。
    [Theory]
    [InlineData(2, AccountType.Margin)] // TrdAccType_Margin
    [InlineData(1, AccountType.Cash)]   // TrdAccType_Cash
    public async Task 口座種別を契約の_AccountType_へ写像する(int sdkAccType, AccountType expected)
    {
        var client = new FakeClient { AccountType = MMApiMoomooTradeClient.MapAccountType(sdkAccType) };

        var state = await new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate).GetAccountStateAsync();

        state!.AccountType.Should().Be(expected);
    }

    // **否定形（fail-closed の要）**: 種別不明は `null`（＝口座種別を確認できていない）へ倒す。
    // **「不明なら信用口座」へ倒してはならない**——現金口座で GFV 回避ガードが無効のまま回る事故になる。
    [Fact]
    public async Task 口座種別が不明なら_null_を返す()
    {
        var client = new FakeClient { AccountType = null };

        var state = await new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate).GetAccountStateAsync();

        state.Should().BeNull();
    }

    // **否定形**: 照会に失敗（例外）しても不明（null）へ倒す。例外を素通ししない（定期 probe を落とさない）。
    [Fact]
    public async Task 口座種別の照会に失敗したら不明として_null_を返す()
    {
        var client = new FakeClient { AccountTypeThrow = () => new InvalidOperationException("OpenD 不達") };

        var state = await new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate).GetAccountStateAsync();

        state.Should().BeNull();
    }

    // **実測に基づく否定形（IADR-0153 決定4）**: moomoo API には決済済み資金・GFV 発生回数の
    // フィールドが存在しない（`TrdCommon.Funds` の全 42 プロパティとアセンブリ全体を走査して確認済み）。
    // したがってアダプタはこれらを**供給しない**。推定値・代替値（`AvlWithdrawalCash` / `MaxCashBuy` 等）で
    // 埋めてはならない——現金口座の買付が「分からないのに通る」ようになり、統制が消える。
    [Fact]
    public async Task 決済済み資金とGFV回数は供給しない()
    {
        var client = new FakeClient { AccountType = MoomooAccountType.Cash };

        var state = await new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate).GetAccountStateAsync();

        state!.SettledCashInBase.Should().BeNull();
    }

    // SDK の `TrdAccType` から本システムの 2 値への写像。**未知の値は null（不明）へ倒す。**
    // 実測した列挙は Unknown=0 / Cash=1 / Margin=2 / TFSA=3 / RRSP=4 / SRRSP=5 / Derivatives=6 であり、
    // ADR-0021 決定2 が想定するのは信用・現金の 2 種だけである（TFSA 等で回すことは計画に無い）。
    [Theory]
    [InlineData(0, null)] // TrdAccType_Unknown —— SDK 自身が「不明が起こり得る」ことを認めている
    [InlineData(1, "Cash")]
    [InlineData(2, "Margin")]
    [InlineData(3, null)] // TFSA
    [InlineData(4, null)] // RRSP
    [InlineData(5, null)] // SRRSP
    [InlineData(6, null)] // Derivatives
    [InlineData(99, null)] // 将来 SDK が増やす未知の値
    public void SDKの口座種別を未知は不明へ倒して写像する(int accType, string? expectedName) =>
        MMApiMoomooTradeClient.MapAccountType(accType)?.ToString().Should().Be(expectedName);
}
