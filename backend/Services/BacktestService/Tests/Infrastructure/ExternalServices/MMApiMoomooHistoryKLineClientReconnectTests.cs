using System.Diagnostics;
using BacktestService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Moomoo.OpenApi;
using Moomoo.OpenApi.Pb;
using Xunit;

namespace BacktestService.Tests;

// #743, FR-15, ADR-0002, ADR-0023 決定5, IADR-0157, IADR-0327: OpenD（相場）接続に失敗した後の
// **接続オブジェクトの作り直し**を固定する。
//
// 直す欠陥: OpenD 停止中に一度 Connection refused を受けた MMAPI_Qot は、以後 InitConnect を呼んでも
// TCP を張り直さない（true を返すだけで SYN も出ない・#732 の実測）。作り直さない限り、OpenD が復旧しても
// 応答待ちのタイムアウトを繰り返すだけで、Pod を入れ直すまで履歴 K 線の取得経路が死ぬ。
// 発注経路（MMApiMoomooTradeClientReconnectTests・#732）と同型の試験である。
//
// 実 OpenD は使わない（IMoomooQotConnectionFactory にフェイクを差す）。
public class MMApiMoomooHistoryKLineClientReconnectTests
{
    // 不達側は応答待ちが要るため最小の 1 秒にする（3 回で高々 3 秒）。復旧側はフェイクが即座に返すため待たない。
    private static MoomooBarDataOptions Options() =>
        new() { OpenDHost = "opend", OpenDPort = 11111, ReplyTimeoutSeconds = 1 };

    private static MoomooHistoryKLineRequest Request() =>
        new("AAPL", new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31), MoomooKLineAdjustment.ForwardAdjusted, 1000, null);

    // FR-15, #743 受け入れ基準 1: 失敗した後の次の試行が**新しい接続オブジェクト**を張り、取得できる。
    [Fact]
    public async Task 接続に失敗した次の試行は接続オブジェクトを作り直して取得できる()
    {
        var factory = new FakeConnectionFactory(FakeConnectBehavior.NeverCompletes);
        var logger = new RecordingLogger<MMApiMoomooHistoryKLineClient>();
        using var client = new MMApiMoomooHistoryKLineClient(Options(), logger, factory);

        // 1 回目: OpenD が落ちている（接続完了が返ってこない）。
        await Assert.ThrowsAsync<TimeoutException>(
            () => client.RequestUsDailyKLinesAsync(Request(), TestContext.Current.CancellationToken));
        factory.Created.Should().HaveCount(1, "起動時に作った 1 本目で試行する");

        // OpenD が復旧した。
        factory.Behavior = FakeConnectBehavior.Succeeds;

        // 2 回目: **入れ直さずに**回復する。
        var page = await client.RequestUsDailyKLinesAsync(Request(), TestContext.Current.CancellationToken);

        page.KLines.Should().ContainSingle().Which.Date.Should().Be(new DateOnly(2024, 1, 4));
        factory.Created.Should().HaveCount(2, "失敗した接続オブジェクトを捨てて作り直す");
        factory.Created[0].Closed.Should().BeTrue("固着した接続は Close して手放す");
        factory.Created[0].Disposed.Should().BeTrue();
        factory.Created[1].InitConnectCalls.Should().Be(1, "作り直した側で InitConnect を張り直す");
        factory.Created[1].RequestHistoryKLCalls.Should().Be(1, "取得は作り直した側から出る");
    }

    // #743 受け入れ基準 4: 固着を切り分けられるログが出る（秘匿情報を含まない）。
    [Fact]
    public async Task 接続オブジェクトを作り直したことがログで区別できる()
    {
        var factory = new FakeConnectionFactory(FakeConnectBehavior.NeverCompletes);
        var logger = new RecordingLogger<MMApiMoomooHistoryKLineClient>();
        using var client = new MMApiMoomooHistoryKLineClient(Options(), logger, factory);

        await Assert.ThrowsAsync<TimeoutException>(
            () => client.RequestUsDailyKLinesAsync(Request(), TestContext.Current.CancellationToken));
        logger.Records.Should().NotContain(r => r.Message.Contains("作り直しました"), "1 回目は作り直していない");

        factory.Behavior = FakeConnectBehavior.Succeeds;
        await client.RequestUsDailyKLinesAsync(Request(), TestContext.Current.CancellationToken);

        var recreated = logger.Records.Where(r => r.Message.Contains("作り直しました")).ToList();
        recreated.Should().ContainSingle();
        recreated[0].Level.Should().Be(LogLevel.Warning);
        recreated[0].Message.Should().Contain("opend").And.Contain("11111").And.Contain("通算作り直し=1");
    }

    // #743 受け入れ基準 2（陰性対照）: OpenD が不達のままなら従来どおり失敗し続ける。
    // **作り直しは復旧の口であって、再試行の口ではない**——回数は呼び出し回数を超えず、ハングしない。
    [Fact]
    public async Task 接続できないままなら毎回失敗し据え置きハングしない()
    {
        var factory = new FakeConnectionFactory(FakeConnectBehavior.NeverCompletes);
        var logger = new RecordingLogger<MMApiMoomooHistoryKLineClient>();
        using var client = new MMApiMoomooHistoryKLineClient(Options(), logger, factory);

        var elapsed = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => client.RequestUsDailyKLinesAsync(Request(), TestContext.Current.CancellationToken));
        }
        elapsed.Stop();

        // 応答待ちは 1 秒 × 3。無限ループ・指数的な再試行になっていないことを上限で押さえる。
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
        factory.Created.Should().HaveCount(3, "作り直しは 1 呼び出しにつき高々 1 回");
        factory.Created.Should().AllSatisfy(c => c.InitConnectCalls.Should().Be(1));
    }

    // #743 受け入れ基準 3（陰性対照）: 接続できない間は**履歴 K 線を要求しない**（外部へ 1 件も出さない）。
    [Fact]
    public async Task 接続できない間は履歴K線の要求がOpenDへ届かない()
    {
        var factory = new FakeConnectionFactory(FakeConnectBehavior.NeverCompletes);
        var logger = new RecordingLogger<MMApiMoomooHistoryKLineClient>();
        using var client = new MMApiMoomooHistoryKLineClient(Options(), logger, factory);

        await Assert.ThrowsAsync<TimeoutException>(
            () => client.RequestUsDailyKLinesAsync(Request(), TestContext.Current.CancellationToken));

        factory.Created.Should().AllSatisfy(c => c.RequestHistoryKLCalls.Should().Be(0));
    }

    private enum FakeConnectBehavior
    {
        /// <summary>接続完了通知を返さない（OpenD 停止中・固着した SDK と同じ見え方）。</summary>
        NeverCompletes,

        /// <summary>接続完了通知を返し、履歴 K 線を 1 本返す。</summary>
        Succeeds,
    }

    private sealed class FakeConnectionFactory(FakeConnectBehavior behavior) : IMoomooQotConnectionFactory
    {
        public FakeConnectBehavior Behavior { get; set; } = behavior;

        public List<FakeQotConnection> Created { get; } = [];

        public IMoomooQotConnection Create()
        {
            // 生成時点の振る舞いを固定する（作り直しの後で OpenD が復旧した、を表現できる）。
            var connection = new FakeQotConnection(Behavior);
            Created.Add(connection);
            return connection;
        }
    }

    private sealed class FakeQotConnection(FakeConnectBehavior behavior) : IMoomooQotConnection
    {
        // コールバックの client 引数。SDK は接続オブジェクト自身を渡す（GetConnectID() が読まれる）。
        private readonly MMAPI_Conn _handle = new();
        private MMSPI_Conn? _connCallback;
        private MMSPI_Qot? _qotCallback;
        private uint _serial;

        public int InitConnectCalls { get; private set; }

        public int RequestHistoryKLCalls { get; private set; }

        public bool Closed { get; private set; }

        public bool Disposed { get; private set; }

        public void SetClientInfo(string clientId, int clientVersion) { }

        public void SetConnCallback(MMSPI_Conn callback) => _connCallback = callback;

        public void SetQotCallback(MMSPI_Qot callback) => _qotCallback = callback;

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

        public uint RequestHistoryKL(QotRequestHistoryKL.Request request)
        {
            RequestHistoryKLCalls++;
            var serial = ++_serial;
            if (behavior == FakeConnectBehavior.Succeeds)
            {
                // 日足 1 本だけ返す（本テストが見るのは接続の張り直しであって写像ではない。
                // 写像は MMApiMoomooHistoryKLineClientMappingTests / MoomooHistoricalBarSourceTests が持つ）。
                var kline = QotCommon.KLine.CreateBuilder()
                    .SetTime("2024-01-04")
                    .SetIsBlank(false)
                    .SetOpenPrice(10.0)
                    .SetHighPrice(11.0)
                    .SetLowPrice(9.0)
                    .SetClosePrice(10.5)
                    .SetVolume(1234L)
                    .BuildPartial();
                var response = QotRequestHistoryKL.Response.CreateBuilder()
                    .SetRetType(0)
                    .SetRetMsg(string.Empty)
                    .SetS2C(QotRequestHistoryKL.S2C.CreateBuilder().AddKlList(kline).BuildPartial())
                    .BuildPartial();
                // 応答は送信の登録が済んだ後で返す必要がある（SendAsync が _sendGate 内で採番・登録する）。
                _ = Task.Run(() => _qotCallback?.OnReply_RequestHistoryKL(_handle, serial, response));
            }
            return serial;
        }

        public void Close() => Closed = true;

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
