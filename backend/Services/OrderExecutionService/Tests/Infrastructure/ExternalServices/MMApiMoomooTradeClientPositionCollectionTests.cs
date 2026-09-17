using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moomoo.OpenApi;
using Moomoo.OpenApi.Pb;
using OrderExecutionService.Infrastructure.ExternalServices;
using Xunit;

namespace OrderExecutionService.Tests;

// #827, FR-05, FR-10, IADR-0118: SIMULATE 口座は建玉照会（TrdGetPositionList）のヘッダ市場を問わず同じ建玉を返す。
// US・JP の両ヘッダで照会して連結すると同じ建玉が 2 回数えられ、risk-management が偽の乖離
// （台帳848≠ブローカ1696）を警告した。照会した市場と一致する建玉だけを採り、防御として (市場, 銘柄, 方向) で畳む。
public class MMApiMoomooTradeClientPositionCollectionTests
{
    private const int Us = (int)TrdCommon.TrdMarket.TrdMarket_US;
    private const int Jp = (int)TrdCommon.TrdMarket.TrdMarket_JP;

    // #827 受け入れ基準 1: 稼働クラスタで観測した形そのもの。
    [Fact]
    public void 同じUS建玉がUSとJPの両ヘッダで返っても1件に数える()
    {
        var positions = MMApiMoomooTradeClient.CollectPositions(
        [
            new MoomooPositionRow(Us, Us, "AAPL", IsShort: false, 848, 333.52m),
            new MoomooPositionRow(Jp, Us, "AAPL", IsShort: false, 848, 333.52m),
        ]);

        positions.Should().ContainSingle();
        positions[0].Should().Be(new MoomooPositionSnapshot("AAPL", MoomooMarket.UnitedStates, 848, 333.52m));
    }

    // #827 受け入れ基準 2: 照会市場と一致する正当な JP 建玉は捨てない。
    [Fact]
    public void JPヘッダにだけ返るJP建玉は残る()
    {
        var positions = MMApiMoomooTradeClient.CollectPositions(
        [
            new MoomooPositionRow(Us, Us, "AAPL", IsShort: false, 10, 150m),
            new MoomooPositionRow(Jp, Jp, "7203", IsShort: false, 100, 2500m),
        ]);

        positions.Should().BeEquivalentTo(
        [
            new MoomooPositionSnapshot("AAPL", MoomooMarket.UnitedStates, 10, 150m),
            new MoomooPositionSnapshot("7203", MoomooMarket.Japan, 100, 2500m),
        ]);
    }

    // #827 受け入れ基準 3: 方向は重複排除のキーに入る。ショートは負の数量のまま残る。
    [Fact]
    public void ショートは負の数量で残り同一銘柄のロングと別に数える()
    {
        var positions = MMApiMoomooTradeClient.CollectPositions(
        [
            new MoomooPositionRow(Us, Us, "TSLA", IsShort: true, 5, 200m),
            new MoomooPositionRow(Us, Us, "TSLA", IsShort: false, 3, 210m),
            new MoomooPositionRow(Jp, Us, "TSLA", IsShort: true, 5, 200m),
            new MoomooPositionRow(Jp, Us, "TSLA", IsShort: false, 3, 210m),
        ]);

        positions.Should().BeEquivalentTo(
        [
            new MoomooPositionSnapshot("TSLA", MoomooMarket.UnitedStates, -5, 200m),
            new MoomooPositionSnapshot("TSLA", MoomooMarket.UnitedStates, 3, 210m),
        ]);
    }

    // #827 受け入れ基準 4: 行の市場が判定できないなら捨てない（捨てると「建玉が無い」と誤報する側へ倒れる）。
    [Fact]
    public void 行の市場が不明なら捨てずに重複だけ畳む()
    {
        var positions = MMApiMoomooTradeClient.CollectPositions(
        [
            new MoomooPositionRow(Us, null, "AAPL", IsShort: false, 848, 333.52m),
            new MoomooPositionRow(Jp, (int)TrdCommon.TrdMarket.TrdMarket_Unknown, "AAPL", IsShort: false, 848, 333.52m),
        ]);

        positions.Should().ContainSingle()
            .Which.Should().Be(new MoomooPositionSnapshot("AAPL", MoomooMarket.UnitedStates, 848, 333.52m));
    }

    // #827 受け入れ基準 5（防御）: フィルタをすり抜けた重複は最初の 1 件だけ採る。
    [Fact]
    public void 同じ市場の応答に同じ建玉が2行あっても最初の1件だけ採る()
    {
        var positions = MMApiMoomooTradeClient.CollectPositions(
        [
            new MoomooPositionRow(Us, Us, "AAPL", IsShort: false, 848, 333.52m),
            new MoomooPositionRow(Us, Us, "AAPL", IsShort: false, 848, 333.60m),
        ]);

        positions.Should().ContainSingle().Which.AverageCost.Should().Be(333.52m);
    }

    [Fact]
    public void 建玉が無ければ空列()
    {
        MMApiMoomooTradeClient.CollectPositions([]).Should().BeEmpty();
    }

    // #827 受け入れ基準 1（照会経路）: protobuf の応答から GetPositionsAsync を通しても 1 件になる。
    // 偽接続は SIMULATE の実測どおり、ヘッダ市場を問わず同じ US 建玉を返す。
    [Fact]
    public async Task 照会経路_SIMULATEが両ヘッダで同じ建玉を返しても1件になる()
    {
        var factory = new SimulateLikeConnectionFactory();
        using var client = new MMApiMoomooTradeClient(
            new MoomooBrokerOptions("opend", 11111) { ReplyTimeout = TimeSpan.FromSeconds(5) },
            NullLogger<MMApiMoomooTradeClient>.Instance,
            factory);

        var positions = await client.GetPositionsAsync(TestContext.Current.CancellationToken);

        factory.Connection.QueriedMarkets.Should().Equal(new[] { Us, Jp }, "全対応市場を照会する契約は不変");
        positions.Should().ContainSingle()
            .Which.Should().Be(new MoomooPositionSnapshot("AAPL", MoomooMarket.UnitedStates, 848, 333.52m));
    }

    private sealed class SimulateLikeConnectionFactory : IMoomooTradeConnectionFactory
    {
        public SimulateLikeConnection Connection { get; } = new();

        public IMoomooTradeConnection Create() => Connection;
    }

    private sealed class SimulateLikeConnection : IMoomooTradeConnection
    {
        private readonly MMAPI_Conn _handle = new();
        private MMSPI_Conn? _connCallback;
        private MMSPI_Trd? _trdCallback;
        private uint _serial;

        public List<int> QueriedMarkets { get; } = [];

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
                .SetAccID(724808UL)
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

        public uint GetPositionList(TrdGetPositionList.Request request)
        {
            var serial = ++_serial;
            QueriedMarkets.Add(request.C2S.Header.TrdMarket);
            // ヘッダ市場を無視して同じ US 建玉を返す（#827 の実測）。
            var position = TrdCommon.Position.CreateBuilder()
                .SetCode("AAPL")
                .SetTrdMarket(Us)
                .SetPositionSide((int)TrdCommon.PositionSide.PositionSide_Long)
                .SetQty(848)
                .SetCostPrice(333.52)
                .BuildPartial();
            var response = TrdGetPositionList.Response.CreateBuilder()
                .SetRetType(0)
                .SetRetMsg(string.Empty)
                .SetS2C(TrdGetPositionList.S2C.CreateBuilder().AddPositionList(position).BuildPartial())
                .BuildPartial();
            _ = Task.Run(() => _trdCallback?.OnReply_GetPositionList(_handle, serial, response));
            return serial;
        }

        public uint PlaceOrder(TrdPlaceOrder.Request request) => ++_serial;

        public uint ModifyOrder(TrdModifyOrder.Request request) => ++_serial;

        public uint GetOrderList(TrdGetOrderList.Request request) => ++_serial;

        public uint GetHistoryOrderList(TrdGetHistoryOrderList.Request request) => ++_serial;

        public void Dispose() { }
    }
}
