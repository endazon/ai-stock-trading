using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moomoo.OpenApi;
using Moomoo.OpenApi.Pb;
using OrderExecutionService.Infrastructure.ExternalServices;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-10, UC-06, ADR-0016 決定3（2026-08-06 改訂）, #967, IADR-0144 決定3, IADR-0425 決定1・2（T-10-1034）:
// MMApiMoomooTradeClient が借株可否を TrdGetMarginRatio で照会すること。
//   - ヘッダは**発注と同じ SIMULATE・発注に使う口座**（実弾ヘッダの照会経路は作らない＝IADR-0425 決定2）。
//   - 非成功の応答（SIMULATE 口座での実測の失敗文言）は例外で返す（「借りられない」と読まない）。
//   - 当該銘柄の行が無い・欄が載っていない、は null（分からない）。
public class MMApiMoomooTradeClientShortPermitTests
{
    private const ulong SimAccId = 724808UL;
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static MMApiMoomooTradeClient Client(MarginRatioConnection connection) =>
        new(new MoomooBrokerOptions("opend", 11111) { ReplyTimeout = Timeout.InfiniteTimeSpan },
            NullLogger<MMApiMoomooTradeClient>.Instance,
            new Factory(connection));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 借株可否を発注と同じ口座のヘッダで照会し答えを返す(bool permit)
    {
        var connection = new MarginRatioConnection(req => Reply(0, "", Row("AAPL", permit)));
        using var client = Client(connection);

        var result = await client.GetShortPermitAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken)
            .WaitAsync(Guard, TestContext.Current.CancellationToken);

        result.Should().Be(permit);
        var sent = connection.Requests.Should().ContainSingle().Subject;
        sent.C2S.Header.TrdEnv.Should().Be((int)TrdCommon.TrdEnv.TrdEnv_Simulate, "実弾ヘッダの照会経路は作らない");
        sent.C2S.Header.AccID.Should().Be(SimAccId, "発注に使っている口座で照会する");
        sent.C2S.Header.TrdMarket.Should().Be((int)TrdCommon.TrdMarket.TrdMarket_US);
        sent.C2S.SecurityListList.Should().ContainSingle()
            .Which.Should().Match<QotCommon.Security>(s =>
                s.Code == "AAPL" && s.Market == (int)QotCommon.QotMarket.QotMarket_US_Security);
    }

    [Fact]
    public async Task 非成功の応答は例外で返し借りられないと読まない()
    {
        // IADR-0144 決定3 の実測（SIMULATE 口座）。
        var connection = new MarginRatioConnection(_ =>
            Reply(-1, "Get Margin Trading Data does not support Stocks in US Market"));
        using var client = Client(connection);

        var act = () => client.GetShortPermitAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken)
            .WaitAsync(Guard, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<MoomooTradeRequestException>();
    }

    [Fact]
    public async Task 当該銘柄の行や欄が無ければ分からないを返す()
    {
        var noRow = new MarginRatioConnection(_ => Reply(0, "", Row("MSFT", true)));
        var noField = new MarginRatioConnection(_ => Reply(0, "", Row("AAPL", null)));
        using var noRowClient = Client(noRow);
        using var noFieldClient = Client(noField);

        (await noRowClient.GetShortPermitAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken)
            .WaitAsync(Guard, TestContext.Current.CancellationToken)).Should().BeNull();
        (await noFieldClient.GetShortPermitAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken)
            .WaitAsync(Guard, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    private static TrdGetMarginRatio.MarginRatioInfo Row(string code, bool? permit)
    {
        var builder = TrdGetMarginRatio.MarginRatioInfo.CreateBuilder()
            .SetSecurity(QotCommon.Security.CreateBuilder()
                .SetMarket((int)QotCommon.QotMarket.QotMarket_US_Security).SetCode(code).Build());
        if (permit is { } p)
            builder.SetIsShortPermit(p);
        return builder.BuildPartial();
    }

    private static TrdGetMarginRatio.Response Reply(int retType, string retMsg, params TrdGetMarginRatio.MarginRatioInfo[] rows)
    {
        var s2c = TrdGetMarginRatio.S2C.CreateBuilder();
        foreach (var row in rows)
            s2c.AddMarginRatioInfoList(row);
        return TrdGetMarginRatio.Response.CreateBuilder()
            .SetRetType(retType)
            .SetRetMsg(retMsg)
            .SetS2C(s2c.BuildPartial())
            .BuildPartial();
    }

    private sealed class Factory(MarginRatioConnection connection) : IMoomooTradeConnectionFactory
    {
        public IMoomooTradeConnection Create() => connection;
    }

    // SIMULATE 口座 1 つを持つ OpenD の偽物。借株可否の照会にだけ意味のある応答を返す。
    private sealed class MarginRatioConnection(Func<TrdGetMarginRatio.Request, TrdGetMarginRatio.Response> reply)
        : IMoomooTradeConnection
    {
        private readonly MMAPI_Conn _handle = new();
        private MMSPI_Conn? _connCallback;
        private MMSPI_Trd? _trdCallback;
        private uint _serial;

        public List<TrdGetMarginRatio.Request> Requests { get; } = [];

        public void SetClientInfo(string clientId, int clientVersion) { }

        public void SetConnCallback(MMSPI_Conn callback) => _connCallback = callback;

        public void SetTrdCallback(MMSPI_Trd callback) => _trdCallback = callback;

        public void SetRsaPrivateKey(string privateKeyPem) { }

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
            var serial = ++_serial;
            var acc = TrdCommon.TrdAcc.CreateBuilder()
                .SetTrdEnv((int)TrdCommon.TrdEnv.TrdEnv_Simulate)
                .SetAccID(SimAccId)
                .SetAccType((int)TrdCommon.TrdAccType.TrdAccType_Margin)
                .BuildPartial();
            var response = TrdGetAccList.Response.CreateBuilder()
                .SetRetType(0)
                .SetRetMsg(string.Empty)
                .SetS2C(TrdGetAccList.S2C.CreateBuilder().AddAccList(acc).BuildPartial())
                .BuildPartial();
            _ = Task.Run(() => _trdCallback?.OnReply_GetAccList(_handle, serial, response));
            return serial;
        }

        public uint GetMarginRatio(TrdGetMarginRatio.Request request)
        {
            var serial = ++_serial;
            Requests.Add(request);
            var response = reply(request);
            _ = Task.Run(() => _trdCallback?.OnReply_GetMarginRatio(_handle, serial, response));
            return serial;
        }

        public uint PlaceOrder(TrdPlaceOrder.Request request) => ++_serial;

        public uint ModifyOrder(TrdModifyOrder.Request request) => ++_serial;

        public uint GetOrderList(TrdGetOrderList.Request request) => ++_serial;

        public uint GetHistoryOrderList(TrdGetHistoryOrderList.Request request) => ++_serial;

        public uint GetPositionList(TrdGetPositionList.Request request) => ++_serial;

        public uint GetFunds(TrdGetFunds.Request request) => ++_serial;

        public void Dispose() { }
    }
}
