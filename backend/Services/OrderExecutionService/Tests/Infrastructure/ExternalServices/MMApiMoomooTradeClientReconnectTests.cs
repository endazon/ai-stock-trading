using System.Diagnostics;
using AiStockTrading.Shared.Contracts.Ports;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Moomoo.OpenApi;
using Moomoo.OpenApi.Pb;
using OrderExecutionService.Infrastructure.ExternalServices;
using Xunit;

namespace OrderExecutionService.Tests;

// #732, FR-05, FR-11, ADR-0002, IADR-0326: OpenD 接続に失敗した後の**接続オブジェクトの作り直し**を固定する。
//
// 直す欠陥: OpenD 停止中に一度 Connection refused を受けた MMAPI_Trd は、以後 InitConnect を呼んでも
// TCP を張り直さない（true を返すだけで SYN も出ない・実測）。作り直さない限り、OpenD が復旧しても
// 応答待ちのタイムアウトを繰り返すだけで、Pod を入れ直すまで発注経路が死ぬ。
//
// 実 OpenD は使わない（IMoomooTradeConnectionFactory にフェイクを差す）。
public class MMApiMoomooTradeClientReconnectTests
{
    private static MoomooBrokerOptions Options() =>
        new("opend", 11111) { ReplyTimeout = TimeSpan.FromMilliseconds(200) };

    // FR-05, #732 受け入れ基準 1: 失敗した後の次の試行が**新しい接続オブジェクト**を張り、接続に成功する。
    [Fact]
    public async Task 接続に失敗した次の試行は接続オブジェクトを作り直して接続できる()
    {
        var factory = new FakeConnectionFactory(FakeConnectBehavior.NeverCompletes);
        var logger = new RecordingLogger<MMApiMoomooTradeClient>();
        using var client = new MMApiMoomooTradeClient(Options(), logger, factory);

        // 1 回目: OpenD が落ちている（接続完了が返ってこない）。
        await Assert.ThrowsAsync<BrokerUnavailableException>(
            () => client.GetPositionsAsync(TestContext.Current.CancellationToken));
        factory.Created.Should().HaveCount(1, "起動時に作った 1 本目で試行する");

        // OpenD が復旧した。
        factory.Behavior = FakeConnectBehavior.Succeeds;

        // 2 回目: **入れ直さずに**回復する。
        var positions = await client.GetPositionsAsync(TestContext.Current.CancellationToken);

        positions.Should().BeEmpty();
        factory.Created.Should().HaveCount(2, "失敗した接続オブジェクトを捨てて作り直す");
        factory.Created[0].Closed.Should().BeTrue("固着した接続は Close して手放す");
        factory.Created[0].Disposed.Should().BeTrue();
        factory.Created[1].InitConnectCalls.Should().Be(1, "作り直した側で InitConnect を張り直す");
    }

    // FR-11, #732 受け入れ基準 3: 固着を切り分けられるログが出る（秘匿情報を含まない）。
    [Fact]
    public async Task 接続オブジェクトを作り直したことがログで区別できる()
    {
        var factory = new FakeConnectionFactory(FakeConnectBehavior.NeverCompletes);
        var logger = new RecordingLogger<MMApiMoomooTradeClient>();
        using var client = new MMApiMoomooTradeClient(Options(), logger, factory);

        await Assert.ThrowsAsync<BrokerUnavailableException>(
            () => client.GetPositionsAsync(TestContext.Current.CancellationToken));
        logger.Records.Should().NotContain(r => r.Message.Contains("作り直しました"), "1 回目は作り直していない");

        factory.Behavior = FakeConnectBehavior.Succeeds;
        await client.GetPositionsAsync(TestContext.Current.CancellationToken);

        var recreated = logger.Records.Where(r => r.Message.Contains("作り直しました")).ToList();
        recreated.Should().ContainSingle();
        recreated[0].Level.Should().Be(LogLevel.Warning);
        recreated[0].Message.Should().Contain("opend").And.Contain("11111").And.Contain("通算作り直し=1");
    }

    // #732 受け入れ基準 2（陰性対照）: OpenD が不達のままなら fail-safe に据え置く。
    // **作り直しは復旧の口であって、再試行の口ではない**——回数は呼び出し回数を超えず、ハングしない。
    [Fact]
    public async Task 接続できないままなら毎回_BrokerUnavailable_で据え置きハングしない()
    {
        var factory = new FakeConnectionFactory(FakeConnectBehavior.NeverCompletes);
        var logger = new RecordingLogger<MMApiMoomooTradeClient>();
        using var client = new MMApiMoomooTradeClient(Options(), logger, factory);

        var elapsed = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<BrokerUnavailableException>(
                () => client.GetPositionsAsync(TestContext.Current.CancellationToken));
        }
        elapsed.Stop();

        // 応答待ちは 200ms × 3。無限ループ・指数的な再試行になっていないことを上限で押さえる。
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        factory.Created.Should().HaveCount(3, "作り直しは 1 呼び出しにつき高々 1 回");
        factory.Created.Should().AllSatisfy(c => c.InitConnectCalls.Should().Be(1));
    }

    // FR-05, IADR-0016 / IADR-0211: 接続できない間は**発注しない**（未発注のまま見送る）。
    [Fact]
    public async Task 接続できない間は発注要求がブローカーへ届かない()
    {
        var factory = new FakeConnectionFactory(FakeConnectBehavior.NeverCompletes);
        var logger = new RecordingLogger<MMApiMoomooTradeClient>();
        using var client = new MMApiMoomooTradeClient(Options(), logger, factory);

        var request = new MoomooOrderRequest("AAPL", MoomooMarket.UnitedStates, MoomooSide.Buy, 1, 100m);
        await Assert.ThrowsAsync<BrokerUnavailableException>(
            () => client.PlaceOrderAsync(request, TestContext.Current.CancellationToken));

        factory.Created.Should().AllSatisfy(c => c.PlaceOrderCalls.Should().Be(0));
    }

    private enum FakeConnectBehavior
    {
        /// <summary>接続完了通知を返さない（OpenD 停止中・固着した SDK と同じ見え方）。</summary>
        NeverCompletes,

        /// <summary>接続完了通知を返し、口座一覧に SIMULATE 口座を 1 件返す。</summary>
        Succeeds,
    }

    private sealed class FakeConnectionFactory(FakeConnectBehavior behavior) : IMoomooTradeConnectionFactory
    {
        public FakeConnectBehavior Behavior { get; set; } = behavior;

        public List<FakeTradeConnection> Created { get; } = [];

        public IMoomooTradeConnection Create()
        {
            // 生成時点の振る舞いを固定する（作り直しの後で OpenD が復旧した、を表現できる）。
            var connection = new FakeTradeConnection(Behavior);
            Created.Add(connection);
            return connection;
        }
    }

    private sealed class FakeTradeConnection(FakeConnectBehavior behavior) : IMoomooTradeConnection
    {
        // コールバックの client 引数。SDK は接続オブジェクト自身を渡す（GetConnectID() が読まれる）。
        private readonly MMAPI_Conn _handle = new();
        private MMSPI_Conn? _connCallback;
        private MMSPI_Trd? _trdCallback;
        private uint _serial;

        public int InitConnectCalls { get; private set; }

        public int PlaceOrderCalls { get; private set; }

        public bool Closed { get; private set; }

        public bool Disposed { get; private set; }

        public void SetClientInfo(string clientId, int clientVersion) { }

        public void SetConnCallback(MMSPI_Conn callback) => _connCallback = callback;

        public void SetTrdCallback(MMSPI_Trd callback) => _trdCallback = callback;

        public void SetRsaPrivateKey(string privateKeyPem) { }

        public bool InitConnect(string host, ushort port, bool encrypt)
        {
            InitConnectCalls++;
            // 🔴 固着した SDK と同じく **true を返す**（戻り値では異常を判定できない）。
            if (behavior == FakeConnectBehavior.Succeeds)
            {
                // 実 SDK と同じく別スレッドから返す（呼び出しの内側で完了させない）。
                _ = Task.Run(() => _connCallback?.OnInitConnect(_handle, 0, string.Empty));
            }
            return true;
        }

        public void Close() => Closed = true;

        public Moomoo.OpenApi.Pb.Common.PacketID NextPacketId() =>
            Moomoo.OpenApi.Pb.Common.PacketID.CreateBuilder().BuildPartial();

        public uint GetAccList(TrdGetAccList.Request request)
        {
            var serial = ++_serial;
            if (behavior == FakeConnectBehavior.Succeeds)
            {
                // SIMULATE（TrdEnv_Simulate=0）の信用口座を 1 件返す。protobuf の required は
                // BuildPartial で省く（本テストが見るのは接続の張り直しであって写像ではない）。
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
                // 応答は送信の登録が済んだ後で返す必要がある（SendAsync が _sendGate 内で採番・登録する）。
                _ = Task.Run(() => _trdCallback?.OnReply_GetAccList(_handle, serial, response));
            }
            return serial;
        }

        public uint GetPositionList(TrdGetPositionList.Request request)
        {
            var serial = ++_serial;
            if (behavior == FakeConnectBehavior.Succeeds)
            {
                var response = TrdGetPositionList.Response.CreateBuilder()
                    .SetRetType(0)
                    .SetRetMsg(string.Empty)
                    .SetS2C(TrdGetPositionList.S2C.CreateBuilder().BuildPartial())
                    .BuildPartial();
                _ = Task.Run(() => _trdCallback?.OnReply_GetPositionList(_handle, serial, response));
            }
            return serial;
        }

        public uint PlaceOrder(TrdPlaceOrder.Request request)
        {
            PlaceOrderCalls++;
            return ++_serial;
        }

        public uint ModifyOrder(TrdModifyOrder.Request request) => ++_serial;

        public uint GetOrderList(TrdGetOrderList.Request request) => ++_serial;

        public uint GetHistoryOrderList(TrdGetHistoryOrderList.Request request) => ++_serial;

        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Records.Add((logLevel, formatter(state, exception)));
    }
}
