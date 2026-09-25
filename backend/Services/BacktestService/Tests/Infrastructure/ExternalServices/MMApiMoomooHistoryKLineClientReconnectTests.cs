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
//
// 🔴 #988, IADR-0379 決定 1・2, IADR-0421: **応答が返ることを表明する試験に、有限の応答待ちを置かない。**
// フェイクの応答は実 SDK と同じく別スレッド（スレッドプール）から返り、応答待ちの打ち切りもスレッドプールが配送する。
// プールが塞がると「1 秒の打ち切り」と「すぐ返るはずの応答」は空いた瞬間に両方とも期限切れで、どちらが先に
// 走るかは保証されない（応答を 1.5 秒遅らせると回復の 2 件が決定的に赤。#981 の発注経路と同じ機序）。
// よって、回復（応答が返る）を表明する試験は応答待ちを無期限（コンストラクタの replyTimeout）にし、1 回目の失敗は
// **接続拒否の通知**で起こす。打ち切り（応答が返らない）で失敗することは、打ち切りだけが完了の口である試験（下の 2 件）で固定する。
public class MMApiMoomooHistoryKLineClientReconnectTests
{
    // 合否の基準ではない。回復が壊れたときに黙って固まる代わりに、理由つきで赤くするための上限（IADR-0379 決定 2）。
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    // 構成の応答待ち（整数秒）。打ち切りだけが完了の口である試験（陰性対照）は、構成の経路（ReplyTimeoutSeconds →
    // TimeSpan）をそのまま通すためにこちらを使う（最小の 1 秒。応答と競走しない）。
    private static MoomooBarDataOptions Options() =>
        new() { OpenDHost = "opend", OpenDPort = 11111, ReplyTimeoutSeconds = 1 };

    private static MoomooHistoryKLineRequest Request() =>
        new("AAPL", new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31), MoomooKLineAdjustment.ForwardAdjusted, 1000, null);

    // 回復を表明する試験のクライアント。応答待ちは無期限（応答と打ち切りを競走させない。IADR-0421）。
    private static MMApiMoomooHistoryKLineClient RecoveringClient(
        FakeConnectionFactory factory, RecordingLogger<MMApiMoomooHistoryKLineClient> logger) =>
        new(Options(), logger, factory, replyTimeout: Timeout.InfiniteTimeSpan);

    // Guard は呼び出しごとに、呼び出しへ渡すキャンセルで掛ける（WaitAsync(Guard) にしない）。応答待ちの打ち切りも
    // Guard の WaitAsync も同じ TimeoutException を投げるため、陰性対照で「打ち切りで失敗した」と「固まって Guard に
    // 切られた」を型で区別できなくなる。キャンセルなら Guard に切られたときは OperationCanceledException で赤くなる。
    private static async Task<MoomooHistoryKLinePage> Guarded(MMApiMoomooHistoryKLineClient client)
    {
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        guard.CancelAfter(Guard);
        return await client.RequestUsDailyKLinesAsync(Request(), guard.Token);
    }

    // FR-15, #743 受け入れ基準 1: 失敗した後の次の試行が**新しい接続オブジェクト**を張り、取得できる。
    // #988: 応答待ちは無期限。打ち切りで失敗した後の作り直しは「接続できないままなら毎回失敗し据え置きハングしない」が
    // 固定する（接続オブジェクトが 3 本）。
    [Fact]
    public async Task 接続に失敗した次の試行は接続オブジェクトを作り直して取得できる()
    {
        var factory = new FakeConnectionFactory(FakeConnectBehavior.Refuses);
        var logger = new RecordingLogger<MMApiMoomooHistoryKLineClient>();
        using var client = RecoveringClient(factory, logger);

        // 1 回目: OpenD が落ちている（接続の失敗が通知される）。
        await Assert.ThrowsAsync<InvalidOperationException>(() => Guarded(client));
        factory.Created.Should().HaveCount(1, "起動時に作った 1 本目で試行する");

        // OpenD が復旧した。
        factory.Behavior = FakeConnectBehavior.Succeeds;

        // 2 回目: **入れ直さずに**回復する。1 本目は拒否を返し続けるので、作り直さなければここで赤くなる。
        var page = await Guarded(client);

        page.KLines.Should().ContainSingle().Which.Date.Should().Be(new DateOnly(2024, 1, 4));
        factory.Created.Should().HaveCount(2, "失敗した接続オブジェクトを捨てて作り直す");
        factory.Created[0].Closed.Should().BeTrue("固着した接続は Close して手放す");
        factory.Created[0].Disposed.Should().BeTrue();
        factory.Created[1].InitConnectCalls.Should().Be(1, "作り直した側で InitConnect を張り直す");
        factory.Created[1].RequestHistoryKLCalls.Should().Be(1, "取得は作り直した側から出る");
    }

    // #743 受け入れ基準 4: 固着を切り分けられるログが出る（秘匿情報を含まない）。
    // #988: 応答待ちは無期限（上の試験と同じ理由）。
    [Fact]
    public async Task 接続オブジェクトを作り直したことがログで区別できる()
    {
        var factory = new FakeConnectionFactory(FakeConnectBehavior.Refuses);
        var logger = new RecordingLogger<MMApiMoomooHistoryKLineClient>();
        using var client = RecoveringClient(factory, logger);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Guarded(client));
        logger.Records.Should().NotContain(r => r.Message.Contains("作り直しました"), "1 回目は作り直していない");

        factory.Behavior = FakeConnectBehavior.Succeeds;
        await Guarded(client);

        var recreated = logger.Records.Where(r => r.Message.Contains("作り直しました")).ToList();
        recreated.Should().ContainSingle();
        recreated[0].Level.Should().Be(LogLevel.Warning);
        recreated[0].Message.Should().Contain("opend").And.Contain("11111").And.Contain("通算作り直し=1");
    }

    // #743 受け入れ基準 2（陰性対照）: OpenD が不達のままなら従来どおり失敗し続ける。
    // **作り直しは復旧の口であって、再試行の口ではない**——回数は呼び出し回数を超えず、ハングしない。
    // 打ち切り（応答待ち 1 秒）で失敗した試行の後も作り直す（2・3 本目）ことを、ここで固定する。
    // #988, IADR-0379 決定 1: 再試行していないことは**回数**で押さえる（壁時計の所要〔旧: 30 秒未満〕では押さえない。
    // 打ち切りの配送はスレッドプールを待つため、所要はプールの混み具合で伸びる）。ハングは Guard が理由つきで赤くする。
    // 失敗の型が TimeoutException（完全一致）であることが「応答ではなく応答待ちの打ち切りで終わった」ことの観測になる
    // （Guard に切られたなら OperationCanceledException になる）。
    [Fact]
    public async Task 接続できないままなら毎回失敗し据え置きハングしない()
    {
        var factory = new FakeConnectionFactory(FakeConnectBehavior.NeverCompletes);
        var logger = new RecordingLogger<MMApiMoomooHistoryKLineClient>();
        using var client = new MMApiMoomooHistoryKLineClient(Options(), logger, factory);

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<TimeoutException>(() => Guarded(client));
        }

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

        await Assert.ThrowsAsync<TimeoutException>(() => Guarded(client));

        factory.Created.Should().AllSatisfy(c => c.RequestHistoryKLCalls.Should().Be(0));
    }

    private enum FakeConnectBehavior
    {
        /// <summary>接続完了通知を返さない（OpenD 停止中・固着した SDK と同じ見え方）。</summary>
        NeverCompletes,

        /// <summary>接続の失敗を通知する（OpenD 停止中に接続を拒否された見え方）。#988: 打ち切りに頼らずに失敗させる。</summary>
        Refuses,

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
            else if (behavior == FakeConnectBehavior.Refuses)
            {
                _ = Task.Run(() => _connCallback?.OnInitConnect(_handle, -1, "Connection refused"));
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
