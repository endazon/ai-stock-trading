using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moomoo.OpenApi;
using Moomoo.OpenApi.Pb;
using OrderExecutionService.Infrastructure.ExternalServices;
using Xunit;

namespace OrderExecutionService.Tests;

// #827, FR-05, FR-10, IADR-0118: SIMULATE 口座は建玉照会（TrdGetPositionList）のヘッダ市場を問わず同じ建玉を返す。
// US・JP の両ヘッダで照会して連結すると同じ建玉が 2 回数えられ、risk-management が偽の乖離
// （台帳848≠ブローカ1696）を警告した。対応市場の行は照会した市場と一致するものだけを採り（対応外の市場は捨てない）、
// PositionID があれば (市場, PositionID)、無ければ (市場, 銘柄, 方向) で畳む。
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

    // #827 監査指摘 1（fail-unsafe の落とし）: 対応市場（US/JP）以外の既知の市場の行は、US・JP どちらの照会でも
    // 「照会市場と異なる」ため従来は両方で捨てられ、実在する建玉が消えた（保護逆指値ガードの残数量 0・偽の LedgerOnly）。
    // 捨てるのは「行の市場が対応市場のいずれかで、かつ照会市場と異なる」ときだけにする。市場の写像は従来どおり。
    [Theory]
    [InlineData((int)TrdCommon.TrdMarket.TrdMarket_HK)]
    [InlineData((int)TrdCommon.TrdMarket.TrdMarket_US_Fund)]
    [InlineData((int)TrdCommon.TrdMarket.TrdMarket_JP_Fund)]
    [InlineData((int)TrdCommon.TrdMarket.TrdMarket_Futures_Simulate_US)]
    public void 対応市場以外の既知の市場の建玉は捨てずに1件だけ残る(int otherMarket)
    {
        var positions = MMApiMoomooTradeClient.CollectPositions(
        [
            new MoomooPositionRow(Us, otherMarket, "00700", IsShort: false, 100, 350m),
            new MoomooPositionRow(Jp, otherMarket, "00700", IsShort: false, 100, 350m),
        ]);

        positions.Should().ContainSingle()
            .Which.Should().Be(new MoomooPositionSnapshot("00700", MoomooMarket.UnitedStates, 100, 350m));
    }

    // #827 監査指摘 2（別ロットの併合）: PositionID があれば (市場, PositionID) で畳む。
    // 同じ銘柄・方向でも PositionID が違えば別の建玉として残す（数量の合算は下流の突合が行う）。
    [Fact]
    public void PositionIDが違えば同じ銘柄と方向でも両方残る()
    {
        var positions = MMApiMoomooTradeClient.CollectPositions(
        [
            new MoomooPositionRow(Us, Us, "AAPL", IsShort: false, 500, 330m, PositionId: 1001UL),
            new MoomooPositionRow(Us, Us, "AAPL", IsShort: false, 348, 340m, PositionId: 1002UL),
        ]);

        positions.Should().BeEquivalentTo(
        [
            new MoomooPositionSnapshot("AAPL", MoomooMarket.UnitedStates, 500, 330m),
            new MoomooPositionSnapshot("AAPL", MoomooMarket.UnitedStates, 348, 340m),
        ]);
        positions.Sum(p => p.Quantity).Should().Be(848);
    }

    // #827 監査指摘 2: 同じ PositionID が US・JP 両ヘッダの応答に現れたら 1 件に数える（行の市場が不明でも）。
    [Fact]
    public void 同じPositionIDがUSとJPの両応答に現れても1件に数える()
    {
        var positions = MMApiMoomooTradeClient.CollectPositions(
        [
            new MoomooPositionRow(Us, null, "AAPL", IsShort: false, 500, 330m, PositionId: 1001UL),
            new MoomooPositionRow(Jp, null, "AAPL", IsShort: false, 500, 330m, PositionId: 1001UL),
            new MoomooPositionRow(Us, null, "AAPL", IsShort: false, 348, 340m, PositionId: 1002UL),
            new MoomooPositionRow(Jp, null, "AAPL", IsShort: false, 348, 340m, PositionId: 1002UL),
        ]);

        positions.Should().HaveCount(2);
        positions.Sum(p => p.Quantity).Should().Be(848);
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

    // #827 監査指摘 2（照会経路）: protobuf の PositionID が行へ写り、別ロットは併合されず、同じロットは両ヘッダで 1 件になる。
    // 配備後の確認用の Debug 要約は件数と市場値の分布だけを出し、銘柄を出さない。
    [Fact]
    public async Task 照会経路_PositionIDが写り別ロットは残り応答要約をDebugに出す()
    {
        var factory = new SimulateLikeConnectionFactory(
            new FakePosition("AAPL", 500, 330, PositionId: 1001UL),
            new FakePosition("AAPL", 348, 340, PositionId: 1002UL));
        var logger = new CapturingLogger();
        using var client = new MMApiMoomooTradeClient(
            new MoomooBrokerOptions("opend", 11111) { ReplyTimeout = TimeSpan.FromSeconds(5) },
            logger,
            factory);

        var positions = await client.GetPositionsAsync(TestContext.Current.CancellationToken);

        positions.Should().HaveCount(2);
        positions.Sum(p => p.Quantity).Should().Be(848);
        var summary = logger.Messages.Should().ContainSingle(m => m.StartsWith("建玉照会の応答要約", StringComparison.Ordinal)).Which;
        summary.Should().Contain("rows=4").And.Contain($"byQueriedMarket={Us}:2,{Jp}:2").And.Contain($"byRowMarket={Us}:4")
            .And.Contain("distinctCodes=1").And.Contain("withPositionId=4").And.Contain("collected=2")
            .And.NotContain("AAPL");
    }

    private sealed record FakePosition(string Code, int Qty, double CostPrice, ulong? PositionId = null);

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<MMApiMoomooTradeClient>
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Debug)
                Messages.Enqueue(formatter(state, exception));
        }
    }

    private sealed class SimulateLikeConnectionFactory(params FakePosition[] positions) : IMoomooTradeConnectionFactory
    {
        public SimulateLikeConnection Connection { get; } = new(positions.Length == 0 ? [new FakePosition("AAPL", 848, 333.52)] : positions);

        public IMoomooTradeConnection Create() => Connection;
    }

    private sealed class SimulateLikeConnection(IReadOnlyList<FakePosition> positions) : IMoomooTradeConnection
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
            var s2c = TrdGetPositionList.S2C.CreateBuilder();
            foreach (var p in positions)
            {
                var position = TrdCommon.Position.CreateBuilder()
                    .SetCode(p.Code)
                    .SetTrdMarket(Us)
                    .SetPositionSide((int)TrdCommon.PositionSide.PositionSide_Long)
                    .SetQty(p.Qty)
                    .SetCostPrice(p.CostPrice);
                if (p.PositionId is { } id)
                    position.SetPositionID(id);
                s2c.AddPositionList(position.BuildPartial());
            }
            var response = TrdGetPositionList.Response.CreateBuilder()
                .SetRetType(0)
                .SetRetMsg(string.Empty)
                .SetS2C(s2c.BuildPartial())
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
