using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moomoo.OpenApi;
using Moomoo.OpenApi.Pb;
using OrderExecutionService.Infrastructure.ExternalServices;
using Xunit;

namespace AiStockTrading.IntegrationTests;

// #754, #342, FR-05, UC-01, UC-02, ADR-0002, ADR-0019, IADR-0016, IADR-0111, IADR-0211, IADR-0327, IADR-0334:
// moomoo 発注アダプタの結合試験。**偽 OpenD**（in-process）に対して
// 接続 → 口座照会 → SIMULATE 発注 → 約定照会 を 1 本で通す。
//
// なぜ要るか: 既存の moomoo 系テストは 2 つに割れており、その間が試験されていない。
//   - `MoomooBrokerAdapterTests` は `IMoomooTradeClient` を fake 化する＝**protobuf を 1 バイトも組まない**
//   - `MMApiMoomooTradeClientMappingTests` は静的写像だけ・`MMApiMoomooTradeClientReconnectTests` は接続の張り直しだけ
// 「選択 → 生成 → アダプタ → SDK 層 → 接続」を通す経路は 1 本も無かった（#342 の 2026-09-11 監査が
// `genuinely open` と判定した残作業）。本テストがその 1 本である。
//
// 🔴 **`Category=Integration` を付けない。** 偽 OpenD は in-process であり Docker を要さない
//（同アセンブリの `E2EInfrastructureTests` と同じ扱い）。trait を付けると既定 CI の
// `--filter "Category!=Integration"` で外れ、**毎 PR の退行防止という目的そのものを失う**。
// `integration.yml` はフィルタを掛けないため、日次側でも走る。
//
// 🔴 **実弾は撃たない。実 OpenD にも稼働クラスタにも触れない**（IADR-0016 / IADR-0056）。
// 偽 OpenD は TCP を張らず、`IMoomooTradeConnectionFactory`（IADR-0327 が開けた差し替え口）に差すだけである。
public sealed class MoomooAdapterFakeOpenDIntegrationTests
{
    // #342 の PoC が実測した SIMULATE（Margin・US）口座の accId。偽 OpenD もこの値を返す。
    private const ulong SimulateAccId = 724808UL;

    // 同 PoC が実測した実弾（Real・Margin）口座の accId。**偽 OpenD は口座一覧の先頭にこれを置く。**
    // 先頭を拾う実装になっていれば実弾口座のヘッダで発注してしまう——それを試験で捕まえるためである。
    private const ulong RealAccId = 284852705357372276UL;

    private static MoomooBrokerOptions Options() =>
        new("opend", 11111) { ReplyTimeout = TimeSpan.FromMilliseconds(500) };

    // 発注先の選択は運用と同じ語彙（Helm の broker.tier=moomoo-sim / env Broker__Provider・Broker__Environment）
    // から組む。**テスト専用の近道を作らない**——閂 0（LiveTradingGate）と Provider の写像もこの経路に乗る。
    private static IBrokerAdapter CreateAdapter(IMoomooTradeClient client, out BrokerSelection selection)
    {
        selection = BrokerSelection.Parse(
            BrokerSelection.MoomooProvider, BrokerSelection.SimulatedEnvironment);
        return BrokerFactory.Create(selection, client, NullLogger<MoomooBrokerAdapter>.Instance);
    }

    private static OrderIntent BuyIntent() =>
        new(
            Symbol: "AAPL",
            Market: Market.UnitedStates,
            Side: TradeSide.Buy,
            ProductType: ProductType.Cash,
            Mode: BrokerProvider.MoomooSimulate,
            Quantity: 10,
            Price: 150m,
            PositionEffect: PositionEffect.Open,
            StopLossPrice: 140m);

    // #754 受け入れ基準 1・3, FR-05, UC-01, UC-02: 接続 → 口座照会 → SIMULATE 発注 → 約定照会の一巡。
    [Fact]
    public async Task 偽OpenDに対して接続から口座照会とSIMULATE発注と約定照会までが一巡する()
    {
        using var opend = new FakeOpenD();
        // 約定は 10 株・平均 149.87（部分約定ではなく全約定）。
        opend.FillOnQuery(quantity: 10, averagePrice: 149.87);
        using var client = new MMApiMoomooTradeClient(Options(), NullLogger<MMApiMoomooTradeClient>.Instance, opend);
        var adapter = CreateAdapter(client, out var selection);

        var placed = await adapter.PlaceOrderAsync(BuyIntent(), TestContext.Current.CancellationToken);

        // 接続 → 口座照会が済んでいる（偽 OpenD 側の実測）。
        opend.Connections.Should().ContainSingle("接続は 1 本で足りる（作り直しは失敗時のみ）");
        opend.Connections[0].InitConnectCalls.Should().Be(1);
        opend.GetAccListCalls.Should().BeGreaterThanOrEqualTo(1, "発注の前に口座を確定する");

        // 発注が SIMULATE として届いている。
        placed.Status.Should().Be(OrderStatus.Accepted, "発注直後は受理（約定は照会で追う）");
        placed.OrderId.Should().Be(opend.LastPlacedOrderId.ToString());
        opend.PlacedOrders.Should().ContainSingle();

        // 約定照会（GetOrderList 経由）でブローカ側の約定が読める。
        var queried = await adapter.GetOrderAsync(placed.OrderId, TestContext.Current.CancellationToken);

        queried.Should().NotBeNull();
        queried!.Status.Should().Be(OrderStatus.Filled);
        queried.FilledQuantity.Should().Be(10);
        queried.AveragePrice.Should().Be(149.87m);

        // FR-20, IADR-0149 決定1: Stage 1 の算入先。paper でも実弾でもない。
        selection.Tier.Should().Be("moomoo-sim");
        ((MoomooBrokerAdapter)adapter).Provider.Should().Be(BrokerProvider.MoomooSimulate);
    }

    // #754 受け入れ基準 2, IADR-0016 / IADR-0056: **発注ヘッダは SIMULATE 口座を指す。**
    // 口座一覧の先頭は実弾（Real・Margin）であり、先頭を拾う実装ならここで落ちる。
    [Fact]
    public async Task 発注ヘッダは実弾口座ではなくSIMULATE口座を指す()
    {
        using var opend = new FakeOpenD();
        using var client = new MMApiMoomooTradeClient(Options(), NullLogger<MMApiMoomooTradeClient>.Instance, opend);
        var adapter = CreateAdapter(client, out _);

        await adapter.PlaceOrderAsync(BuyIntent(), TestContext.Current.CancellationToken);

        var sent = opend.PlacedOrders.Should().ContainSingle().Subject;
        sent.Header.TrdEnv.Should().Be((int)TrdCommon.TrdEnv.TrdEnv_Simulate, "実弾（TrdEnv_Real）は撃たない");
        sent.Header.AccID.Should().Be(SimulateAccId);
        sent.Header.AccID.Should().NotBe(RealAccId, "口座一覧の先頭（実弾）を拾ってはならない");
        // 意図が protobuf まで欠落なく届いている（写像の live 検証は別だが、組み立ては固定できる）。
        sent.Code.Should().Be("AAPL");
        sent.TrdSide.Should().Be((int)TrdCommon.TrdSide.TrdSide_Buy);
        sent.OrderType.Should().Be((int)TrdCommon.OrderType.OrderType_Normal);
        sent.Qty.Should().Be(10d);
        sent.Price.Should().Be(150d);
        sent.SecMarket.Should().Be((int)TrdCommon.TrdSecMarket.TrdSecMarket_US);
    }

    // UC-02, #292, IADR-0118: 建玉照会も同じ経路（可用性 probe が使う口）で成立する。
    [Fact]
    public async Task 建玉照会も同じ経路で成立し可用性が真になる()
    {
        using var opend = new FakeOpenD();
        opend.Position(symbol: "AAPL", quantity: 10, costPrice: 149.87, isShort: false);
        using var client = new MMApiMoomooTradeClient(Options(), NullLogger<MMApiMoomooTradeClient>.Instance, opend);
        var adapter = (MoomooBrokerAdapter)CreateAdapter(client, out _);

        var positions = await adapter.GetPositionsAsync(TestContext.Current.CancellationToken);

        positions.Should().NotBeNull("照会不能（null）と建玉ゼロ（空列）は別物である");
        // 対応市場は US / JP の 2 つ。偽 OpenD は US にだけ建玉を返す。
        positions!.Should().ContainSingle();
        positions[0].Symbol.Should().Be("AAPL");
        positions[0].Quantity.Should().Be(10);
        (await adapter.IsOperationalAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    // T-10-508, T-10-514, FR-10, #869, #897, ADR-0041 決定2, IADR-0354 決定1:
    // **基準資金（equity）は「応答が別通貨を名乗っていない」ときに採る。**
    //
    // 🔴 本経路でしか通貨の検証は試験できない（`MoomooBrokerAdapterTests` は `IMoomooTradeClient` を
    // fake 化するため protobuf を 1 バイトも組まず、通貨の分岐に到達しない）。
    //
    // 🔴 **［2026-09-19 / #897］通貨の欠落は「要求した通貨（USD）」として採る。**
    // `Funds.currency` は protobuf の **optional** であり（required は power / totalAssets / cash /
    // marketVal / frozenCash / debtCash / avlWithdrawalCash の 7 つ）、**実機の OpenD は載せてこない**。
    // 「名乗っているときだけ採る」（#874）は実機に対して**常に fail-closed** になり、基準資金が
    // 一度も供給されなかった（稼働環境で実測）。要求は `Currency_USD` を明示しており、
    // moomoo の契約では応答はその通貨で返る —— **近似として採る**（IADR-0354 決定1 の追記）。
    //
    // 🔴 **別通貨を明示したケースは従来どおり採らない**（この守りは実機でも意味がある）。
    //
    // 🔴 **［#898 監査］`Currency_Unknown(0)` は第 4 のケースである** ——「欄は送ったが値を決められなかった」
    // 場合に現れ得る。**欄が「無い」のとは別物**であり、こちらは未供給へ倒す（決められなかったと
    // 名乗っている応答を「USD だろう」と読む理由が無い）。
    [Theory]
    [InlineData("usd", 3_000)]    // 応答が USD と名乗る → 採る
    [InlineData("jpy", null)]     // 応答が別通貨と名乗る → 採らない（守りは残す）
    [InlineData("unknown", null)] // 応答が「決められない」と名乗る → 採らない（欄が無いのとは別物）
    [InlineData("unset", 3_000)]  // 🔴 応答が通貨を名乗らない（＝実機）→ 要求した通貨を前提として採る
    public async Task 基準資金は応答が別通貨を名乗らないときに採る(string currency, int? expected)
    {
        using var opend = new FakeOpenD
        {
            EquityInBase = 3_000m,
            FundsCurrency = currency switch
            {
                "usd" => (int)TrdCommon.Currency.Currency_USD,
                "jpy" => (int)TrdCommon.Currency.Currency_JPY,
                "unknown" => (int)TrdCommon.Currency.Currency_Unknown,
                _ => null,
            },
        };
        using var client = new MMApiMoomooTradeClient(Options(), NullLogger<MMApiMoomooTradeClient>.Instance, opend);
        var adapter = (MoomooBrokerAdapter)CreateAdapter(client, out _);

        var state = await adapter.GetAccountStateAsync(TestContext.Current.CancellationToken);

        state.Should().NotBeNull("口座種別は確認できている（評価額の可否で種別まで捨てない）");
        state!.AccountType.Should().Be(AccountType.Margin);
        state.EquityInBase.Should().Be(expected is { } e ? e : null);
    }

    // T-10-515, FR-10, #869, IADR-0354 決定7: **資産純値が 0 以下なら供給しない**（未供給へ倒す）。
    // 🔴 0 を分母にすると比率上限がすべて 0 になり、平常状態でも `DailyLossLimitReached` が立ち、
    // 発注審査が**翌営業日まで続くロックアウト**を張る。口座種別は（応答があるので）残る。
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task 資産純値が0以下なら基準資金を供給しない(int equity)
    {
        using var opend = new FakeOpenD { EquityInBase = equity };
        using var client = new MMApiMoomooTradeClient(Options(), NullLogger<MMApiMoomooTradeClient>.Instance, opend);
        var adapter = (MoomooBrokerAdapter)CreateAdapter(client, out _);

        var state = await adapter.GetAccountStateAsync(TestContext.Current.CancellationToken);

        state.Should().NotBeNull();
        state!.AccountType.Should().Be(AccountType.Margin);
        state.EquityInBase.Should().BeNull();
    }

    // T-10-620, FR-10, #897, IADR-0354 決定1:
    // 🔴 **偽 OpenD の既定は実機と同じ（通貨を送らない）であり、その既定のままで基準資金が供給される。**
    //
    // 本テストが守っているのは実装ではなく**試験環境そのもの**である。既定が `Currency_USD` へ戻されると、
    // 通貨に触れない他のすべてのケースが「実機では起きない、通貨を送る OpenD」に対して緑になり、
    // #897 と同型の事故（実機でだけ常時 fail-closed）が再び見えなくなる。
    [Fact]
    public async Task 偽OpenDの既定は実機と同じく通貨を送らずその既定で基準資金が供給される()
    {
        using var opend = new FakeOpenD();

        opend.FundsCurrency.Should().BeNull(
            "実機の OpenD は TrdGetFunds の応答に currency（protobuf の optional）を載せない。"
                + "偽物の既定を本物より行儀良くしない");

        using var client = new MMApiMoomooTradeClient(Options(), NullLogger<MMApiMoomooTradeClient>.Instance, opend);
        var adapter = (MoomooBrokerAdapter)CreateAdapter(client, out _);

        var state = await adapter.GetAccountStateAsync(TestContext.Current.CancellationToken);

        state.Should().NotBeNull();
        state!.EquityInBase.Should().Be(3_000m, "要求した通貨（USD）を前提として採る");
    }

    // T-10-621, FR-10, #897, IADR-0354 決定1（近似の記録）:
    // 🔴 **近似で採ったことは毎回ログに残る。ただし Warning ではなく Information である。**
    //
    // 通貨の欠落は実機の**正常な見え方**であり、既定 5 分の巡回で必ず出る。Warning のままだと
    // 警告が常時鳴って本物の警告が埋もれる（#874 が作った状態がまさにそれ）。黙らせもしない ——
    // 「いま採っている値が近似かどうか」をログの 1 行で答えられなくなるためである。
    [Fact]
    public async Task 通貨が欠けている応答から採ったことはInformationで記録されWarningにはならない()
    {
        using var opend = new FakeOpenD();
        var logger = new RecordingLogger<MMApiMoomooTradeClient>();
        using var client = new MMApiMoomooTradeClient(Options(), logger, opend);
        var adapter = (MoomooBrokerAdapter)CreateAdapter(client, out _);

        var state = await adapter.GetAccountStateAsync(TestContext.Current.CancellationToken);

        state!.EquityInBase.Should().Be(3_000m);
        logger.Entries.Should().ContainSingle(e =>
                e.Level == LogLevel.Information && e.Message.Contains("要求した通貨"),
            "近似で採ったことを毎回残す");
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning,
            "実機の正常な見え方を警告として鳴らし続けない（本物の警告が埋もれる）");
    }

    // #754 陰性対照, FR-05, IADR-0211: OpenD が受け付けないなら**注文は 1 度も送られない**。
    [Fact]
    public async Task 陰性対照_OpenDが応答しないと発注は1度もブローカーへ届かない()
    {
        using var opend = new FakeOpenD { Refusing = true };
        using var client = new MMApiMoomooTradeClient(Options(), NullLogger<MMApiMoomooTradeClient>.Instance, opend);
        var adapter = CreateAdapter(client, out _);

        await Assert.ThrowsAsync<BrokerUnavailableException>(
            () => adapter.PlaceOrderAsync(BuyIntent(), TestContext.Current.CancellationToken));

        opend.PlacedOrders.Should().BeEmpty("接続が確立していない間は注文を組み立てもしない");
        opend.Connections.Should().AllSatisfy(c => c.PlaceOrderCalls.Should().Be(0));
    }

    // #754 陰性対照, IADR-0211: 不達を Rejected へ**丸めない**。
    // Rejected は「証券会社が受理しなかった状態」（FR-05）であり、届いてすらいない事象を混ぜると
    // 拒否件数の集計が接続障害で汚染される。
    [Fact]
    public async Task 陰性対照_OpenD不達はRejectedへ丸めずBrokerUnavailableのまま伝播する()
    {
        using var opend = new FakeOpenD { Refusing = true };
        using var client = new MMApiMoomooTradeClient(Options(), NullLogger<MMApiMoomooTradeClient>.Instance, opend);
        var adapter = CreateAdapter(client, out _);

        var thrown = await Assert.ThrowsAsync<BrokerUnavailableException>(
            () => adapter.PlaceOrderAsync(BuyIntent(), TestContext.Current.CancellationToken));

        thrown.Should().NotBeNull();
        // 建玉照会は「不明（null）」へ倒れる——こちらは丸めてよい（乖離報告を止める安全側）。
        (await ((MoomooBrokerAdapter)adapter).GetPositionsAsync(TestContext.Current.CancellationToken))
            .Should().BeNull();
        (await ((MoomooBrokerAdapter)adapter).IsOperationalAsync(TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }

    /// <summary>
    /// ログの水準と本文を実測するための <see cref="ILogger{TCategoryName}"/>（#897 / T-10-621）。
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>
    /// 偽 OpenD。<see cref="IMoomooTradeConnectionFactory"/>（IADR-0327）へ差し込み、TCP も Docker も使わずに
    /// OpenD の応答を再現する。**protobuf はそのまま通す**——SDK 非依存の境界は
    /// <see cref="IMoomooTradeClient"/> が持っており、ここで二重に作らない（同 IADR 決定1）。
    /// </summary>
    private sealed class FakeOpenD : IMoomooTradeConnectionFactory, IDisposable
    {
        private readonly List<FakeConnection> _connections = [];

        /// <summary>OpenD が停止している（接続完了通知が返らない）。#732 が実測した見え方と同じ。</summary>
        public bool Refusing { get; init; }

        public IReadOnlyList<FakeConnection> Connections => _connections;

        public int GetAccListCalls { get; private set; }

        /// <summary>受け取った発注の C2S（protobuf）。**組み立てを実測するための記録である。**</summary>
        public List<TrdPlaceOrder.C2S> PlacedOrders { get; } = [];

        public ulong LastPlacedOrderId { get; private set; }

        private (int Quantity, double AveragePrice)? _fill;
        private (string Symbol, int Quantity, double CostPrice, bool IsShort)? _position;
        private ulong _nextOrderId = 9_000_000_001UL;

        /// <summary>約定照会（GetOrderList）で返す約定を仕込む。</summary>
        public void FillOnQuery(int quantity, double averagePrice) => _fill = (quantity, averagePrice);

        /// <summary>建玉照会（GetPositionList）で US 市場に返す建玉を仕込む。</summary>
        public void Position(string symbol, int quantity, double costPrice, bool isShort) =>
            _position = (symbol, quantity, costPrice, isShort);

        /// <summary>
        /// FR-10, #869, ADR-0041 決定2, IADR-0354: 口座照会（GetFunds）が返す資産純値（USD）。
        /// 統制上限の基準資金の供給元である。
        /// </summary>
        public decimal EquityInBase { get; set; } = 3_000m;

        /// <summary>
        /// FR-10, #869, #897, IADR-0354 決定1: 口座照会（GetFunds）の応答が名乗る通貨。
        /// <para>
        /// 🔴 <c>null</c> は<b>通貨フィールドを設定しない</b>応答である（<c>Funds.currency</c> は protobuf の
        /// <b>optional</b> であり、required は power / totalAssets / cash / marketVal / frozenCash /
        /// debtCash / avlWithdrawalCash の 7 つだけ）。
        /// </para>
        /// <para>
        /// 🔴 <b>［2026-09-19 / #897］既定は <c>null</c>（＝欄を設定しない）である。これは実機の OpenD と同じ
        /// 見え方である。</b> 従前の既定は <c>Currency_USD</c> であり、<b>偽物が本物より行儀が良かった</b> ——
        /// そのため #874 が入れた通貨ガードが実機で常時 fail-closed になっていることに、
        /// 是正・2 巡の監査・差分監査のいずれも気づけなかった（すべて偽 OpenD 上で緑だった）。
        /// <b>既定を実機へ寄せておかないと、同じ事故がまた起きる。</b> 既定そのものは T-10-620 が固定する。
        /// </para>
        /// </summary>
        public int? FundsCurrency { get; set; }

        public IMoomooTradeConnection Create()
        {
            var connection = new FakeConnection(this);
            _connections.Add(connection);
            return connection;
        }

        public void Dispose()
        {
            foreach (var connection in _connections)
                connection.Dispose();
        }

        private ulong NextOrderId() => _nextOrderId++;

        internal sealed class FakeConnection(FakeOpenD opend) : IMoomooTradeConnection
        {
            // コールバックの client 引数。SDK は接続オブジェクト自身を渡す（GetConnectID() が読まれる）。
            private readonly MMAPI_Conn _handle = new();
            private MMSPI_Conn? _connCallback;
            private MMSPI_Trd? _trdCallback;
            private uint _serial;
            private uint _packetSerial;

            // 偽の接続 ID（OpenD が `OnInitConnect` で配る値の代わり）。値そのものに意味は無い。
            private const ulong FakeConnectionId = 7503815433946088959UL;

            public int InitConnectCalls { get; private set; }

            public int PlaceOrderCalls { get; private set; }

            public void SetClientInfo(string clientId, int clientVersion) { }

            public void SetConnCallback(MMSPI_Conn callback) => _connCallback = callback;

            public void SetTrdCallback(MMSPI_Trd callback) => _trdCallback = callback;

            public void SetRsaPrivateKey(string privateKeyPem) { }

            public bool InitConnect(string host, ushort port, bool encrypt)
            {
                InitConnectCalls++;
                // 🔴 停止中の OpenD と同じく **true を返す**（戻り値では異常を判定できない・#732 の実測）。
                if (!opend.Refusing)
                {
                    // 実 SDK と同じく別スレッドから返す（呼び出しの内側で完了させない）。
                    _ = Task.Run(() => _connCallback?.OnInitConnect(_handle, 0, string.Empty));
                }
                return true;
            }

            public void Close() { }

            // 🔴 発注・取消の C2S は `Build()`（required 充足を要求する）で組まれるため、**packetID は
            // 完全に埋めて返す**。BuildPartial で欠いたまま返すと `TrdPlaceOrder.C2S.Build()` が
            // UninitializedMessageException を投げ、アダプタがそれを Rejected へ丸めて**偽の緑**になる。
            public Moomoo.OpenApi.Pb.Common.PacketID NextPacketId() =>
                Moomoo.OpenApi.Pb.Common.PacketID.CreateBuilder()
                    .SetConnID(FakeConnectionId)
                    .SetSerialNo(++_packetSerial)
                    .Build();

            // #342 の PoC が実測した 3 口座のうち、US の 2 つを返す。**実弾（Real）が先頭**である。
            public uint GetAccList(TrdGetAccList.Request request)
            {
                opend.GetAccListCalls++;
                var serial = ++_serial;
                var real = TrdCommon.TrdAcc.CreateBuilder()
                    .SetTrdEnv((int)TrdCommon.TrdEnv.TrdEnv_Real)
                    .SetAccID(RealAccId)
                    .SetAccType((int)TrdCommon.TrdAccType.TrdAccType_Margin)
                    .BuildPartial();
                var simulate = TrdCommon.TrdAcc.CreateBuilder()
                    .SetTrdEnv((int)TrdCommon.TrdEnv.TrdEnv_Simulate)
                    .SetAccID(SimulateAccId)
                    .SetAccType((int)TrdCommon.TrdAccType.TrdAccType_Margin)
                    .BuildPartial();
                var response = TrdGetAccList.Response.CreateBuilder()
                    .SetRetType(0)
                    .SetRetMsg(string.Empty)
                    .SetS2C(TrdGetAccList.S2C.CreateBuilder().AddAccList(real).AddAccList(simulate).BuildPartial())
                    .BuildPartial();
                Reply(() => _trdCallback?.OnReply_GetAccList(_handle, serial, response));
                return serial;
            }

            public uint PlaceOrder(TrdPlaceOrder.Request request)
            {
                PlaceOrderCalls++;
                var serial = ++_serial;
                var orderId = opend.NextOrderId();
                opend.LastPlacedOrderId = orderId;
                opend.PlacedOrders.Add(request.C2S);
                var response = TrdPlaceOrder.Response.CreateBuilder()
                    .SetRetType(0)
                    .SetRetMsg(string.Empty)
                    .SetS2C(TrdPlaceOrder.S2C.CreateBuilder().SetOrderID(orderId).BuildPartial())
                    .BuildPartial();
                Reply(() => _trdCallback?.OnReply_PlaceOrder(_handle, serial, response));
                return serial;
            }

            public uint ModifyOrder(TrdModifyOrder.Request request)
            {
                var serial = ++_serial;
                var response = TrdModifyOrder.Response.CreateBuilder()
                    .SetRetType(0)
                    .SetRetMsg(string.Empty)
                    .BuildPartial();
                Reply(() => _trdCallback?.OnReply_ModifyOrder(_handle, serial, response));
                return serial;
            }

            // 約定照会。直前に発注した注文を、仕込まれた約定つきで返す（US 市場のときだけ）。
            public uint GetOrderList(TrdGetOrderList.Request request)
            {
                var serial = ++_serial;
                var builder = TrdGetOrderList.S2C.CreateBuilder();
                var isUnitedStates = request.C2S.Header.TrdMarket == (int)TrdCommon.TrdMarket.TrdMarket_US;
                if (isUnitedStates && opend.LastPlacedOrderId != 0 && opend.PlacedOrders.Count > 0)
                {
                    builder.AddOrderList(BuildOrder(opend.PlacedOrders[^1], opend.LastPlacedOrderId, opend._fill));
                }
                var response = TrdGetOrderList.Response.CreateBuilder()
                    .SetRetType(0)
                    .SetRetMsg(string.Empty)
                    .SetS2C(builder.BuildPartial())
                    .BuildPartial();
                Reply(() => _trdCallback?.OnReply_GetOrderList(_handle, serial, response));
                return serial;
            }

            public uint GetHistoryOrderList(TrdGetHistoryOrderList.Request request)
            {
                var serial = ++_serial;
                var response = TrdGetHistoryOrderList.Response.CreateBuilder()
                    .SetRetType(0)
                    .SetRetMsg(string.Empty)
                    .SetS2C(TrdGetHistoryOrderList.S2C.CreateBuilder().BuildPartial())
                    .BuildPartial();
                Reply(() => _trdCallback?.OnReply_GetHistoryOrderList(_handle, serial, response));
                return serial;
            }

            public uint GetPositionList(TrdGetPositionList.Request request)
            {
                var serial = ++_serial;
                var builder = TrdGetPositionList.S2C.CreateBuilder();
                var isUnitedStates = request.C2S.Header.TrdMarket == (int)TrdCommon.TrdMarket.TrdMarket_US;
                if (isUnitedStates && opend._position is { } p)
                {
                    builder.AddPositionList(TrdCommon.Position.CreateBuilder()
                        .SetCode(p.Symbol)
                        .SetTrdMarket((int)TrdCommon.TrdMarket.TrdMarket_US)
                        .SetPositionSide((int)(p.IsShort
                            ? TrdCommon.PositionSide.PositionSide_Short
                            : TrdCommon.PositionSide.PositionSide_Long))
                        .SetQty(p.Quantity)
                        .SetCostPrice(p.CostPrice)
                        .BuildPartial());
                }
                var response = TrdGetPositionList.Response.CreateBuilder()
                    .SetRetType(0)
                    .SetRetMsg(string.Empty)
                    .SetS2C(builder.BuildPartial())
                    .BuildPartial();
                Reply(() => _trdCallback?.OnReply_GetPositionList(_handle, serial, response));
                return serial;
            }

            // FR-10, #869, ADR-0041 決定2, IADR-0354: 口座の評価額（基準資金の供給元）を返す。
            public uint GetFunds(TrdGetFunds.Request request)
            {
                var serial = ++_serial;
                var fundsBuilder = TrdCommon.Funds.CreateBuilder()
                    .SetTotalAssets((double)opend.EquityInBase);
                // 🔴 null のときは currency を**設定しない**（optional フィールドの欠落を再現する）。
                if (opend.FundsCurrency is { } currency)
                {
                    fundsBuilder = fundsBuilder.SetCurrency(currency);
                }
                var funds = fundsBuilder.BuildPartial();
                var response = TrdGetFunds.Response.CreateBuilder()
                    .SetRetType(0)
                    .SetRetMsg(string.Empty)
                    .SetS2C(TrdGetFunds.S2C.CreateBuilder().SetFunds(funds).BuildPartial())
                    .BuildPartial();
                Reply(() => _trdCallback?.OnReply_GetFunds(_handle, serial, response));
                return serial;
            }

            public void Dispose() { }

            // 応答は送信の登録が済んだ後で返す必要がある（SendAsync が _sendGate 内で採番・登録する）。
            private static void Reply(Action reply) => _ = Task.Run(reply);

            // 発注した C2S から、ブローカ側の注文（全約定）を組み立てる。
            // protobuf の required は BuildPartial で省く（本テストが見るのは経路であって required の充足ではない）。
            private static TrdCommon.Order BuildOrder(
                TrdPlaceOrder.C2S placed, ulong orderId, (int Quantity, double AveragePrice)? fill)
            {
                var builder = TrdCommon.Order.CreateBuilder()
                    .SetOrderID(orderId)
                    .SetCode(placed.Code)
                    .SetTrdMarket(placed.Header.TrdMarket)
                    .SetTrdSide(placed.TrdSide)
                    .SetQty(placed.Qty)
                    // OrderStatus_Filled_All=11（MMApiMoomooTradeClient.MapState の写像対象）。
                    .SetOrderStatus(fill is null
                        ? (int)TrdCommon.OrderStatus.OrderStatus_Submitted
                        : (int)TrdCommon.OrderStatus.OrderStatus_Filled_All)
                    .SetFillQty(fill?.Quantity ?? 0)
                    .SetFillAvgPrice(fill?.AveragePrice ?? 0d);
                if (placed.HasPrice)
                    builder.SetPrice(placed.Price);
                if (placed.HasRemark)
                    builder.SetRemark(placed.Remark);
                return builder.BuildPartial();
            }
        }
    }
}
