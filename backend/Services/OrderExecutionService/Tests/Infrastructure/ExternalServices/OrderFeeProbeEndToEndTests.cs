using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Moomoo.OpenApi;
using Moomoo.OpenApi.Pb;
using OrderExecutionService.Features.OrderExecution.ProbeOrderFee;
using OrderExecutionService.Infrastructure.ExternalServices;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-11, FR-16, ADR-0016 決定15, #1086, IADR-0300（2026-09-29 追記）: 検証口（OrderFeeProbeCommand）＋ 構成からの組み立て
// （OrderFeeProbeComposition）＋ 実物の MMApiMoomooTradeClient を、偽の OpenD（IMoomooTradeConnection）に繋いで通す。
// OpenD へは実接続しない。
//   - Trd_GetOrderFee は 1 回だけ・書き込み系（PlaceOrder / ModifyOrder）は 0 回
//   - 照会の鍵は OrderID ではなく注文一覧から引いた OrderIDEx・ヘッダは SIMULATE・発注に使う口座
//   - 口座 ID の全桁と RSA 鍵の内容を出さない
public class OrderFeeProbeEndToEndTests
{
    private const ulong SimAccId = 283745190123UL;
    private const ulong RealAccId = 918273645501UL;
    private const ulong OrderId = 7788990011UL;
    private const string OrderIdEx = "20260929_NVDA_EX1";
    // 鍵ファイルの中身の見張り値。gitleaks の generic-api-key に当たらないよう、識別子に key 系の語を使わず低エントロピーのダミーにする。
    private const string PemMarker = "dummy-pem-dummy-pem-dummy-pem";

    private static TrdGetOrderFee.Response FeeReply(int retType, string retMsg, params (string Title, double Value)[] items)
    {
        var fee = TrdCommon.OrderFee.CreateBuilder().SetOrderIDEx(OrderIdEx).SetFeeAmount(items.Sum(i => i.Value));
        foreach (var (title, value) in items)
            fee.AddFeeList(TrdCommon.OrderFeeItem.CreateBuilder().SetTitle(title).SetValue(value).BuildPartial());
        var s2c = TrdGetOrderFee.S2C.CreateBuilder();
        if (items.Length > 0)
            s2c.AddOrderFeeList(fee.BuildPartial());
        return TrdGetOrderFee.Response.CreateBuilder()
            .SetRetType(retType).SetRetMsg(retMsg).SetS2C(s2c.BuildPartial()).BuildPartial();
    }

    private const string ProbeHost = "opend-probe-host.internal";
    private const string ProbePort = "23456";

    private static async Task<(int ExitCode, string Output)> Probe(FakeOpenD opend, string orderId, string? keyPath = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Broker:Provider"] = "moomoo",
            ["Broker:Environment"] = "sim",
            ["Broker:Moomoo:OpenD:Host"] = ProbeHost,
            ["Broker:Moomoo:OpenD:Port"] = ProbePort,
            ["Broker:Moomoo:OpenD:ReplyTimeoutSeconds"] = "2",
            ["Broker:Moomoo:OpenD:RsaPrivateKeyPath"] = keyPath,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var writer = new StringWriter();
        var exitCode = await OrderFeeProbeCommand.RunAsync(
            [OrderFeeProbeCommand.Flag, orderId],
            () => OrderFeeProbeComposition.CreateQuery(configuration, new Factory(opend)),
            writer,
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken,
            OrderFeeProbeComposition.SensitiveValues(configuration));
        return (exitCode, writer.ToString());
    }

    [Fact]
    public async Task OrderIDから引いたOrderIDExで1回だけ照会し費用項目を出す()
    {
        var opend = new FakeOpenD(historyOrderIdEx: OrderIdEx, feeReply: _ => FeeReply(0, "", ("Commission", 0.99), ("Settlement Fee", 0.03)));

        var (exitCode, output) = await Probe(opend, OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        exitCode.Should().Be(OrderFeeProbeCommand.ExitFeesReturned, output);
        opend.FeeRequests.Should().ContainSingle("Trd_GetOrderFee は 1 回だけ");
        var sent = opend.FeeRequests[0].C2S;
        sent.OrderIdExListList.Should().Equal(OrderIdEx);
        sent.Header.TrdEnv.Should().Be((int)TrdCommon.TrdEnv.TrdEnv_Simulate, "実弾ヘッダの照会経路は作らない");
        sent.Header.AccID.Should().Be(SimAccId, "発注に使っている口座で照会する");
        sent.Header.TrdMarket.Should().Be((int)TrdCommon.TrdMarket.TrdMarket_US);
        opend.WriteCalls.Should().Be(0, "書き込み系（PlaceOrder / ModifyOrder）を呼ばない");
        opend.ListCalls.Should().Be(2, "US の当日 1 回・履歴 1 回で見つかる（撃ち直さない）");
        output.Should().Contain("fee[0].item[0].title=Commission")
            .And.Contain("fee[0].item[1].title=Settlement Fee")
            .And.Contain($"order.orderIdEx={OrderIdEx}")
            .And.Contain("order.market=US");
    }

    [Fact]
    public async Task 非成功の応答でも照会は1回で口座IDの全桁を出さない()
    {
        var opend = new FakeOpenD(
            historyOrderIdEx: OrderIdEx,
            feeReply: _ => FeeReply(-1, $"acc {SimAccId} does not support this protocol in simulate"));

        var (exitCode, output) = await Probe(opend, OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        exitCode.Should().Be(OrderFeeProbeCommand.ExitQueryFailed, output);
        opend.FeeRequests.Should().ContainSingle("失敗しても撃ち直さない");
        opend.WriteCalls.Should().Be(0);
        output.Should().Contain("retType=-1").And.Contain("result=failed").And.Contain("acc ****23 does not support");
        output.Should().NotContain(SimAccId.ToString(System.Globalization.CultureInfo.InvariantCulture), "口座 ID は伏せる");
    }

    [Theory]
    [InlineData(ListFailure.Current)]
    [InlineData(ListFailure.History)]
    public async Task 注文一覧の照会の失敗文に口座IDが含まれても全桁を出さない(ListFailure failure)
    {
        // #1086 AI レビュー 🔴: 数字の OrderID から OrderIDEx を引く経路の失敗は、生の retMsg を含む例外として上がる。
        var opend = new FakeOpenD(historyOrderIdEx: OrderIdEx, feeReply: _ => FeeReply(0, "", ("Commission", 1.0)))
        {
            Failure = failure,
        };

        var (exitCode, output) = await Probe(opend, OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        exitCode.Should().Be(OrderFeeProbeCommand.ExitQueryFailed, output);
        opend.FeeRequests.Should().BeEmpty("解決に失敗したら費用照会は撃たない");
        output.Should().Contain("result=error").And.Contain("acc ****23 is not authorized");
        output.Should().NotContain(SimAccId.ToString(System.Globalization.CultureInfo.InvariantCulture), "口座 ID は伏せる");
    }

    [Fact]
    public async Task 口座一覧の照会の失敗文に口座IDが含まれても全桁を出さない()
    {
        // 別文脈監査: 口座が確定する前（GetAccList の失敗）は伏せる値が分からない。例外文の長い数字の並びを伏せる。
        var opend = new FakeOpenD(historyOrderIdEx: OrderIdEx, feeReply: null) { AccListFails = true };

        var (exitCode, output) = await Probe(opend, OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        exitCode.Should().Be(1, output);
        opend.FeeRequests.Should().BeEmpty();
        output.Should().Contain("acc ****23 is not authorized");
        output.Should().NotContain(SimAccId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task 接続先のhostとportを出力に載せず例外の型と要約は残す()
    {
        var opend = new FakeOpenD(historyOrderIdEx: OrderIdEx, feeReply: null) { InitConnectFails = true };

        var (exitCode, output) = await Probe(opend, OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        exitCode.Should().Be(1, output);
        output.Should().NotContain(ProbeHost).And.NotContain(ProbePort);
        output.Should().Contain("error[0].type=BrokerUnavailableException").And.Contain("InitConnect が失敗しました（<伏せ>）");
    }

    [Fact]
    public async Task RSA鍵のパスが無いときの構成不正でもパスを出力に載せない()
    {
        const string missingPath = "/run/probe-test-dir/moomoo-rsa-9f3.pem";
        var opend = new FakeOpenD(historyOrderIdEx: OrderIdEx, feeReply: null);

        var (exitCode, output) = await Probe(opend, "123", missingPath);

        exitCode.Should().Be(2, output);
        opend.Created.Should().BeFalse("preflight で止まり接続オブジェクトを作らない");
        output.Should().Contain("result=config-error").And.Contain("RsaPrivateKeyPath '<伏せ>'");
        output.Should().NotContain(missingPath).And.NotContain("moomoo-rsa-9f3");
    }

    [Fact]
    public async Task 履歴の照会窓は過去30日()
    {
        var opend = new FakeOpenD(historyOrderIdEx: OrderIdEx, feeReply: _ => FeeReply(0, "", ("Commission", 1.0)));
        var before = DateTimeOffset.UtcNow;

        await Probe(opend, OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var filter = opend.HistoryRequests.Should().ContainSingle().Subject.C2S.FilterConditions;
        var begin = DateTimeOffset.ParseExact(filter.BeginTime, "yyyy-MM-dd HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
        (before - begin).TotalDays.Should().BeInRange(29.99, 30.01, "履歴を引く窓は 30 日（短くすると直近の約定でも取りこぼす）");
    }

    [Fact]
    public async Task 照会口の結果のretMsgは口座IDを伏せて返す()
    {
        // 伏せは 2 層（照会口の結果・検証口の出力の最終段）。この試験は照会口の層だけを見る（検証口を通さない）。
        var opend = new FakeOpenD(historyOrderIdEx: OrderIdEx, feeReply: _ => FeeReply(-1, $"acc {SimAccId} denied"));
        using var client = (MMApiMoomooTradeClient)OrderFeeProbeComposition.CreateQuery(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Broker:Provider"] = "moomoo",
                ["Broker:Environment"] = "sim",
                ["Broker:Moomoo:OpenD:ReplyTimeoutSeconds"] = "2",
            }).Build(),
            new Factory(opend));

        var result = await client.QueryOrderFeeAsync("EX_direct-01", TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(OrderFeeQueryOutcome.Failed);
        result.RetMsg.Should().Be("acc ****23 denied");
        result.MaskedAccountId.Should().Be("****23");
    }

    // T-10-2006, FR-11, #1148, IADR-0476: 照会口の retMsg は、口座一覧で見た実弾口座の ID も伏せる
    //（独立監査 🟡: 従来は SIMULATE の口座 ID だけを伏せており、実弾の口座 ID が検証口の出力に全桁で出得た）。
    [Fact]
    public async Task 照会口の結果のretMsgは実弾口座のIDも伏せる()
    {
        var opend = new FakeOpenD(historyOrderIdEx: OrderIdEx, feeReply: _ => FeeReply(-1, $"acc {SimAccId} / real {RealAccId} denied"))
        {
            IncludeRealAccount = true,
        };
        using var client = (MMApiMoomooTradeClient)OrderFeeProbeComposition.CreateQuery(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Broker:Provider"] = "moomoo",
                ["Broker:Environment"] = "sim",
                ["Broker:Moomoo:OpenD:ReplyTimeoutSeconds"] = "2",
            }).Build(),
            new Factory(opend));

        var result = await client.QueryOrderFeeAsync("EX_direct-01", TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(OrderFeeQueryOutcome.Failed);
        result.RetMsg.Should().Be("acc ****23 / real ****01 denied");
    }

    [Fact]
    public async Task 出力の最終段は例外以外の行に現れた口座IDも伏せる()
    {
        // 伏せの最終段だけを見る: 費用項目名は照会口が伏せない（応答のまま）ため、最終段が無ければ全桁が出る。
        var opend = new FakeOpenD(historyOrderIdEx: OrderIdEx,
            feeReply: _ => FeeReply(0, "", ($"Fee for {SimAccId}", 1.0)));

        var (exitCode, output) = await Probe(opend, "EX_direct-01");

        exitCode.Should().Be(0, output);
        output.Should().Contain("fee[0].item[0].title=Fee for ****23");
        output.Should().NotContain(SimAccId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task RSA鍵の内容と口座IDを出力に載せない()
    {
        var keyPath = Path.Combine(Path.GetTempPath(), $"order-fee-probe-{Guid.NewGuid():N}.pem");
        await File.WriteAllTextAsync(keyPath, PemMarker, TestContext.Current.CancellationToken);
        try
        {
            var opend = new FakeOpenD(historyOrderIdEx: OrderIdEx, feeReply: _ => FeeReply(0, "", ("Commission", 1.0)));

            var (exitCode, output) = await Probe(opend, OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture), keyPath);

            exitCode.Should().Be(OrderFeeProbeCommand.ExitFeesReturned, output);
            opend.RsaKeysApplied.Should().Equal(PemMarker);
            output.Should().NotContain(PemMarker);
            output.Should().NotContain(SimAccId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            output.Should().Contain("account=SIMULATE(****23)");
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public async Task 返信が来なければ1回で打ち切り失敗として終わる()
    {
        var opend = new FakeOpenD(historyOrderIdEx: OrderIdEx, feeReply: null);

        var (exitCode, output) = await Probe(opend, OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        exitCode.Should().Be(OrderFeeProbeCommand.ExitQueryFailed, output);
        opend.FeeRequests.Should().ContainSingle("タイムアウトでも撃ち直さない");
        output.Should().Contain("result=error").And.Contain("error[0].type=TimeoutException");
    }

    [Fact]
    public async Task 注文が見つからなければ照会を撃たない()
    {
        var opend = new FakeOpenD(historyOrderIdEx: OrderIdEx, feeReply: _ => FeeReply(0, "", ("Commission", 1.0)));

        var (exitCode, output) = await Probe(opend, "1234");

        exitCode.Should().Be(OrderFeeProbeCommand.ExitQueryFailed, output);
        opend.FeeRequests.Should().BeEmpty();
        opend.WriteCalls.Should().Be(0);
        opend.ListCalls.Should().Be(4, "注文一覧の照会は最大 4 回（US・JP × 当日・履歴）で、再試行しない");
        output.Should().Contain("result=order-not-found");
        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task OrderIDExが空なら照会を撃たない()
    {
        var opend = new FakeOpenD(historyOrderIdEx: null, feeReply: _ => FeeReply(0, "", ("Commission", 1.0)));

        var (exitCode, output) = await Probe(opend, OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        exitCode.Should().Be(OrderFeeProbeCommand.ExitQueryFailed, output);
        opend.FeeRequests.Should().BeEmpty();
        output.Should().Contain("result=order-id-ex-missing").And.Contain("order.status=11");
    }

    [Fact]
    public async Task OrderIDExを直接与えれば注文一覧を引かずにそのまま照会する()
    {
        var opend = new FakeOpenD(historyOrderIdEx: OrderIdEx, feeReply: _ => FeeReply(0, ""));

        var (exitCode, output) = await Probe(opend, "EX_direct-01");

        exitCode.Should().Be(OrderFeeProbeCommand.ExitNoFees, output);
        opend.ListCalls.Should().Be(0);
        opend.FeeRequests.Should().ContainSingle().Which.C2S.OrderIdExListList.Should().Equal("EX_direct-01");
    }

    [Theory]
    [InlineData("paper", "sim")]
    [InlineData("moomoo", "live")]
    public void moomooのSIMULATE以外では照会口を組まない(string provider, string environment)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Broker:Provider"] = provider,
            ["Broker:Environment"] = environment,
        }).Build();
        var opend = new FakeOpenD(historyOrderIdEx: OrderIdEx, feeReply: null);

        var act = () => OrderFeeProbeComposition.CreateQuery(configuration, new Factory(opend));

        act.Should().Throw<InvalidOperationException>();
        opend.Created.Should().BeFalse("接続オブジェクトも作らない");
    }

    private static IConfiguration OpenDConfig(string? host, string? port, string? keyPath = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Broker:Moomoo:OpenD:Host"] = host,
            ["Broker:Moomoo:OpenD:Port"] = port,
            ["Broker:Moomoo:OpenD:RsaPrivateKeyPath"] = keyPath,
        }).Build();

    [Fact]
    public void 構成で与えたhostはhost単体とhostとportの組の両方を伏せる値にする()
    {
        // 差分監査 M11: host だけが現れる文（port を伴わない）も伏せる。
        OrderFeeProbeComposition.SensitiveValues(OpenDConfig(ProbeHost, ProbePort, "/run/k/rsa.pem"))
            .Should().BeEquivalentTo([$"{ProbeHost}:{ProbePort}", ProbeHost, "/run/k/rsa.pem"]);
    }

    [Fact]
    public async Task host単体が現れる例外文でもhostを出さない()
    {
        var writer = new StringWriter();
        await OrderFeeProbeCommand.RunAsync(
            [OrderFeeProbeCommand.Flag, "123"],
            () => throw new InvalidOperationException($"host {ProbeHost} unreachable"),
            writer,
            cancellationToken: TestContext.Current.CancellationToken,
            sensitiveValues: OrderFeeProbeComposition.SensitiveValues(OpenDConfig(ProbeHost, ProbePort)));

        writer.ToString().Should().Contain("host <伏せ> unreachable").And.NotContain(ProbeHost);
    }

    [Fact]
    public void 未設定の既定のhostは伏せる値にしない()
    {
        // 差分監査 M12 / 2 の決定: 既定 `opend`・11111 はチャートとコードに公開の Service 名・ポートで秘密ではない。
        // 一般語として伏せると出力が読めなくなるため、構成で与えた値だけを伏せる。port だけ与えたら既定 host との組を伏せる。
        OrderFeeProbeComposition.SensitiveValues(OpenDConfig(null, null)).Should().BeEmpty();
        OrderFeeProbeComposition.SensitiveValues(OpenDConfig(null, ProbePort)).Should().Equal($"opend:{ProbePort}");
    }

    [Fact]
    public void 口座IDの伏せは末尾2桁だけを残す()
    {
        MMApiMoomooTradeClient.MaskAccountId(SimAccId).Should().Be("****23");
        MMApiMoomooTradeClient.MaskAccountId(7).Should().Be("****");
        MMApiMoomooTradeClient.RedactAccountId($"x{SimAccId}y", SimAccId).Should().Be("x****23y");
        MMApiMoomooTradeClient.RedactAccountId(null, SimAccId).Should().BeNull();
    }

    public enum ListFailure { None, Current, History }

    private sealed class Factory(FakeOpenD opend) : IMoomooTradeConnectionFactory
    {
        public IMoomooTradeConnection Create()
        {
            opend.Created = true;
            return opend;
        }
    }

    // SIMULATE 口座 1 つ・約定済み注文 1 件（US の履歴）を持つ OpenD の偽物。
    // feeReply が null なら費用照会に返信しない（返信待ちのタイムアウトを起こす）。
    private sealed class FakeOpenD(string? historyOrderIdEx, Func<TrdGetOrderFee.Request, TrdGetOrderFee.Response>? feeReply)
        : IMoomooTradeConnection
    {
        private readonly MMAPI_Conn _handle = new();
        private MMSPI_Conn? _connCallback;
        private MMSPI_Trd? _trdCallback;
        private int _serial;

        public bool Created { get; set; }

        // 口座一覧に実弾口座も載せる（#1148: 検証口も実弾の口座 ID を伏せる）。
        public bool IncludeRealAccount { get; init; }

        // 注文一覧の照会を非成功（口座 ID を含む retMsg）で返す位置。
        public ListFailure Failure { get; init; }

        public bool AccListFails { get; init; }

        public bool InitConnectFails { get; init; }

        public List<TrdGetHistoryOrderList.Request> HistoryRequests { get; } = [];

        private static readonly string DeniedMessage = $"acc {SimAccId} is not authorized for this query";

        public List<TrdGetOrderFee.Request> FeeRequests { get; } = [];

        public List<string> RsaKeysApplied { get; } = [];

        public int WriteCalls;

        public int ListCalls;

        private uint NextSerial() => (uint)Interlocked.Increment(ref _serial);

        public void SetClientInfo(string clientId, int clientVersion) { }

        public void SetConnCallback(MMSPI_Conn callback) => _connCallback = callback;

        public void SetTrdCallback(MMSPI_Trd callback) => _trdCallback = callback;

        public void SetRsaPrivateKey(string privateKeyPem) => RsaKeysApplied.Add(privateKeyPem);

        public bool InitConnect(string host, ushort port, bool encrypt)
        {
            if (InitConnectFails)
                return false;
            _ = Task.Run(() => _connCallback?.OnInitConnect(_handle, 0, string.Empty));
            return true;
        }

        public void Close() { }

        public Moomoo.OpenApi.Pb.Common.PacketID NextPacketId() =>
            Moomoo.OpenApi.Pb.Common.PacketID.CreateBuilder().BuildPartial();

        public uint GetAccList(TrdGetAccList.Request request)
        {
            var serial = NextSerial();
            var acc = TrdCommon.TrdAcc.CreateBuilder()
                .SetTrdEnv((int)TrdCommon.TrdEnv.TrdEnv_Simulate)
                .SetAccID(SimAccId)
                .SetAccType((int)TrdCommon.TrdAccType.TrdAccType_Margin)
                .BuildPartial();
            var s2c = TrdGetAccList.S2C.CreateBuilder().AddAccList(acc);
            if (IncludeRealAccount)
            {
                s2c.AddAccList(TrdCommon.TrdAcc.CreateBuilder()
                    .SetTrdEnv((int)TrdCommon.TrdEnv.TrdEnv_Real)
                    .SetAccID(RealAccId)
                    .SetAccType((int)TrdCommon.TrdAccType.TrdAccType_Margin)
                    .BuildPartial());
            }
            var response = TrdGetAccList.Response.CreateBuilder()
                .SetRetType(AccListFails ? -1 : 0).SetRetMsg(AccListFails ? DeniedMessage : string.Empty)
                .SetS2C(s2c.BuildPartial())
                .BuildPartial();
            _ = Task.Run(() => _trdCallback?.OnReply_GetAccList(_handle, serial, response));
            return serial;
        }

        // 当日の注文一覧は空（約定は前日の米国時間＝履歴側にある想定）。
        public uint GetOrderList(TrdGetOrderList.Request request)
        {
            Interlocked.Increment(ref ListCalls);
            var serial = NextSerial();
            var failed = Failure == ListFailure.Current;
            var response = TrdGetOrderList.Response.CreateBuilder()
                .SetRetType(failed ? -1 : 0).SetRetMsg(failed ? DeniedMessage : string.Empty)
                .SetS2C(TrdGetOrderList.S2C.CreateBuilder().SetHeader(request.C2S.Header).BuildPartial())
                .BuildPartial();
            _ = Task.Run(() => _trdCallback?.OnReply_GetOrderList(_handle, serial, response));
            return serial;
        }

        public uint GetHistoryOrderList(TrdGetHistoryOrderList.Request request)
        {
            Interlocked.Increment(ref ListCalls);
            lock (HistoryRequests)
                HistoryRequests.Add(request);
            var serial = NextSerial();
            var s2c = TrdGetHistoryOrderList.S2C.CreateBuilder().SetHeader(request.C2S.Header);
            if (request.C2S.Header.TrdMarket == (int)TrdCommon.TrdMarket.TrdMarket_US)
            {
                var order = TrdCommon.Order.CreateBuilder()
                    .SetOrderID(OrderId)
                    .SetCode("NVDA")
                    .SetTrdSide((int)TrdCommon.TrdSide.TrdSide_Buy)
                    .SetOrderStatus(11)
                    .SetTrdMarket((int)TrdCommon.TrdMarket.TrdMarket_US);
                if (historyOrderIdEx is not null)
                    order.SetOrderIDEx(historyOrderIdEx);
                s2c.AddOrderList(order.BuildPartial());
            }
            var failed = Failure == ListFailure.History;
            var response = TrdGetHistoryOrderList.Response.CreateBuilder()
                .SetRetType(failed ? -1 : 0).SetRetMsg(failed ? DeniedMessage : string.Empty)
                .SetS2C(s2c.BuildPartial()).BuildPartial();
            _ = Task.Run(() => _trdCallback?.OnReply_GetHistoryOrderList(_handle, serial, response));
            return serial;
        }

        public uint GetOrderFee(TrdGetOrderFee.Request request)
        {
            var serial = NextSerial();
            lock (FeeRequests)
                FeeRequests.Add(request);
            if (feeReply is not null)
            {
                var response = feeReply(request);
                _ = Task.Run(() => _trdCallback?.OnReply_GetOrderFee(_handle, serial, response));
            }
            return serial;
        }

        public uint PlaceOrder(TrdPlaceOrder.Request request)
        {
            Interlocked.Increment(ref WriteCalls);
            return NextSerial();
        }

        public uint ModifyOrder(TrdModifyOrder.Request request)
        {
            Interlocked.Increment(ref WriteCalls);
            return NextSerial();
        }

        public uint GetPositionList(TrdGetPositionList.Request request) => NextSerial();

        public uint GetFunds(TrdGetFunds.Request request) => NextSerial();

        public uint GetMarginRatio(TrdGetMarginRatio.Request request) => NextSerial();

        public void Dispose() { }
    }
}
