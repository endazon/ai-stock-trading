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

    private static async Task<(int ExitCode, string Output)> Probe(FakeOpenD opend, string orderId, string? keyPath = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Broker:Provider"] = "moomoo",
            ["Broker:Environment"] = "sim",
            ["Broker:Moomoo:OpenD:Host"] = "opend",
            ["Broker:Moomoo:OpenD:Port"] = "11111",
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
            TestContext.Current.CancellationToken);
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
        output.Should().Contain("result=order-not-found");
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

        // 注文一覧の照会を非成功（口座 ID を含む retMsg）で返す位置。
        public ListFailure Failure { get; init; }

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
            var response = TrdGetAccList.Response.CreateBuilder()
                .SetRetType(0).SetRetMsg(string.Empty)
                .SetS2C(TrdGetAccList.S2C.CreateBuilder().AddAccList(acc).BuildPartial())
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
