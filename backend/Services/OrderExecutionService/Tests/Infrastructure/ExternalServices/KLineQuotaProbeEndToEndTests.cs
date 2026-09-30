using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Moomoo.OpenApi;
using Moomoo.OpenApi.Pb;
using OrderExecutionService.Features.OrderExecution.ProbeKLineQuota;
using OrderExecutionService.Infrastructure.ExternalServices;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-02, FR-15, ADR-0048 決定 3, ADR-0023 決定 5, #1117, IADR-0464: 検証口（KLineQuotaProbeCommand）＋ 構成からの組み立て
// （KLineQuotaProbeComposition）＋ 実物の MMApiMoomooKLineProbeClient を、偽の OpenD（IMoomooQotProbeConnection）に繋いで通す。
// OpenD へは実接続しない。
//   - 送る要求の中身（市場・コード・日足・復権区分・期間・項目の旗・詳細の旗）と回数
//   - 応答の写像（出来高・売買代金・空白足・続きの鍵）と枠の差
//   - 接続先・RSA 鍵のパス・鍵の内容を出さない
//   - paper / 実弾階層では組まない・実装の型が発注の面を持たない
public class KLineQuotaProbeEndToEndTests
{
    private const string ProbeHost = "opend-kline-probe.internal";
    private const string ProbePort = "23457";
    // 鍵ファイルの中身の見張り値（gitleaks の generic-api-key に当たらないよう低エントロピーのダミー）。
    private const string PemMarker = "dummy-pem-dummy-pem-dummy-pem";

    private static IConfiguration Config(string? keyPath = null, string provider = "moomoo", string environment = "sim") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Broker:Provider"] = provider,
            ["Broker:Environment"] = environment,
            ["Broker:Moomoo:OpenD:Host"] = ProbeHost,
            ["Broker:Moomoo:OpenD:Port"] = ProbePort,
            ["Broker:Moomoo:OpenD:ReplyTimeoutSeconds"] = "5",
            ["Broker:Moomoo:OpenD:RsaPrivateKeyPath"] = keyPath,
        }).Build();

    private static async Task<(int ExitCode, string Output)> Probe(FakeOpenD opend, string? keyPath = null, params string[] extraArgs)
    {
        var configuration = Config(keyPath);
        var writer = new StringWriter();
        var exitCode = await KLineQuotaProbeCommand.RunAsync(
            [KLineQuotaProbeCommand.Flag, .. extraArgs],
            () => KLineQuotaProbeComposition.CreateQuery(configuration, new Factory(opend)),
            writer,
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken,
            KLineQuotaProbeComposition.SensitiveValues(configuration),
            new FixedTimeProvider(new DateOnly(2026, 9, 30)),
            (_, _) => Task.CompletedTask);
        return (exitCode, writer.ToString());
    }

    [Fact]
    public async Task 日足と取得枠の要求を決まった中身で1回ずつ送る()
    {
        var opend = new FakeOpenD();

        var (exitCode, output) = await Probe(opend);

        exitCode.Should().Be(KLineQuotaProbeCommand.ExitAllSucceeded, output);
        opend.InitConnectCalls.Should().Be(1, "接続は 1 回");
        opend.QuotaRequests.Select(r => r.C2S.BGetDetail).Should().Equal(true, false, false, false, false, false, true);
        opend.KLineRequests.Should().HaveCount(5);
        opend.KLineRequests.Select(r => r.C2S.Security.Code).Should().Equal("AAPL", "MSFT", "NVDA", "NVDA", "NVDA");
        opend.KLineRequests.Select(r => r.C2S.RehabType).Should().Equal(
            (int)QotCommon.RehabType.RehabType_Forward, (int)QotCommon.RehabType.RehabType_Forward,
            (int)QotCommon.RehabType.RehabType_None, (int)QotCommon.RehabType.RehabType_Forward,
            (int)QotCommon.RehabType.RehabType_Backward);
        opend.KLineRequests.Should().AllSatisfy(r =>
        {
            r.C2S.Security.Market.Should().Be((int)QotCommon.QotMarket.QotMarket_US_Security);
            r.C2S.KlType.Should().Be((int)QotCommon.KLType.KLType_Day);
            r.C2S.MaxAckKLNum.Should().Be(1000);
            r.C2S.HasNextReqKey.Should().BeFalse("続きの鍵は追わない（1 リクエストだけ）");
            (r.C2S.NeedKLFieldsFlag & (long)QotCommon.KLFields.KLFields_Volume).Should().NotBe(0);
            (r.C2S.NeedKLFieldsFlag & (long)QotCommon.KLFields.KLFields_Turnover).Should().NotBe(0, "売買代金も出す");
        });
        opend.KLineRequests[0].C2S.BeginTime.Should().Be("2026-07-28");
        opend.KLineRequests[0].C2S.EndTime.Should().Be("2026-09-30");
        opend.KLineRequests[2].C2S.BeginTime.Should().Be("2024-05-28");
        opend.KLineRequests[2].C2S.EndTime.Should().Be("2024-06-21");
        output.Should().Contain("result=ok");
    }

    [Fact]
    public async Task quota_onlyは詳細つきの枠の照会1件だけを送りK線の要求を1件も送らない()
    {
        // #1125（IADR-0464 の 2026-09-30 追記）: 取り直すと requestTime が更新され回復の時計が戻るため、K 線を送らない。
        var opend = new FakeOpenD();

        var (exitCode, output) = await Probe(opend, null, KLineQuotaProbeCommand.QuotaOnlyOption);

        exitCode.Should().Be(KLineQuotaProbeCommand.ExitAllSucceeded, output);
        opend.InitConnectCalls.Should().Be(1);
        opend.QuotaRequests.Select(r => r.C2S.BGetDetail).Should().Equal([true], "詳細つきの照会 1 回だけ");
        opend.KLineRequests.Should().BeEmpty("K 線は 1 本も取らない");
        output.Should().Contain("quota[0] label=quota-only retType=0 used=7 remain=293")
            .And.Contain("quota[0].detail[0] security=US.AAPL name=Apple requestTime=2026-09-29 22:10:05 requestTimeStamp=1790719805 "
                + "requestTime.tz=UTC+8 requestTime.jst=2026-09-29 23:10:05 requestTime.utc=2026-09-29 14:10:05")
            .And.Contain("quota.used=7 quota.remain=293 quota.total=300")
            .And.Contain("requests.sent=1 requests.failed=0");
        output.Should().NotContain(ProbeHost).And.NotContain(ProbePort);
    }

    [Fact]
    public async Task 応答の出来高と売買代金と空白足と続きの鍵と枠の詳細を写す()
    {
        var opend = new FakeOpenD();

        var (_, output) = await Probe(opend, null, "--symbols", "AAPL", "--count", "2");

        output.Should().Contain("quota[0] label=before retType=0 used=7 remain=293")
            .And.Contain("quota[0].detail[0] security=US.AAPL name=Apple requestTime=2026-09-29 22:10:05 requestTimeStamp=1790719805")
            .And.Contain("kline[0] symbol=AAPL rehab=forward from=2026-09-12 to=2026-09-30 retType=0 bars=2 blank=1 hasMore=yes")
            .And.Contain("kline[0].bar[1] time=2026-09-29 00:00:00 open=254.1 high=256.5 low=253 close=255.75 volume=41234567 turnover=10546789012.5")
            .And.Contain("quota[1] label=after-kline[0] retType=0 used=7 remain=293 delta.used=0")
            .And.Contain("quota.step[0] symbol=AAPL rehab=forward known=yes delta.used=0");
    }

    [Fact]
    public async Task 非成功の応答はretTypeとretMsgを出し撃ち直さない()
    {
        var opend = new FakeOpenD { KLineRetType = -1, KLineRetMsg = "No permission" };

        var (exitCode, output) = await Probe(opend);

        exitCode.Should().Be(KLineQuotaProbeCommand.ExitQueryFailed, output);
        opend.KLineRequests.Should().HaveCount(5, "非成功でも撃ち直さない（各段 1 回）");
        output.Should().Contain("kline[0].retMsg=No permission").And.Contain("result=partial");
    }

    [Fact]
    public async Task 接続先のhostとportを出力に載せず例外の型と要約は残す()
    {
        var opend = new FakeOpenD { InitConnectFails = true };

        var (exitCode, output) = await Probe(opend);

        exitCode.Should().Be(KLineQuotaProbeCommand.ExitQueryFailed, output);
        opend.QuotaRequests.Should().BeEmpty("接続できなければ要求を 1 件も送らない");
        opend.KLineRequests.Should().BeEmpty();
        output.Should().NotContain(ProbeHost).And.NotContain(ProbePort);
        output.Should().Contain("error[0].type=InvalidOperationException").And.Contain("InitConnect が失敗しました（<伏せ>）");
    }

    [Fact]
    public async Task 返信が来なければ打ち切り以後を撃たずに終わる()
    {
        var opend = new FakeOpenD { NeverReply = true };
        var configuration = Config();
        var writer = new StringWriter();
        using var client = new MMApiMoomooKLineProbeClient(
            MoomooBrokerOptions.FromConfiguration(configuration), new Factory(opend), TimeSpan.FromMilliseconds(200));

        var exitCode = await KLineQuotaProbeCommand.RunAsync(
            [KLineQuotaProbeCommand.Flag], () => client, writer, TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken, null, new FixedTimeProvider(new DateOnly(2026, 9, 30)), (_, _) => Task.CompletedTask);

        exitCode.Should().Be(KLineQuotaProbeCommand.ExitQueryFailed);
        opend.QuotaRequests.Should().ContainSingle("タイムアウトでも撃ち直さない・以後の段も撃たない");
        opend.KLineRequests.Should().BeEmpty();
        writer.ToString().Should().Contain("error[0].type=TimeoutException").And.Contain("requests.sent=1");
    }

    [Fact]
    public async Task 応答待ちの間に切断されたら返信待ちの打ち切りを待たずに失敗し以後を撃たない()
    {
        // PR #1119 の監査 F2: 切断で応答待ちの要求を即座に失敗させる（返信待ちの打ち切り〔構成 5 秒〕まで待たない）。
        var opend = new FakeOpenD { DisconnectOnKLine = true };

        var (exitCode, output) = await Probe(opend);

        exitCode.Should().Be(KLineQuotaProbeCommand.ExitQueryFailed, output);
        output.Should().Contain("result=error")
            .And.Contain("error[0].type=InvalidOperationException")
            .And.Contain("接続が切れた")
            .And.Contain("requests.sent=2");
        output.Should().NotContain("TimeoutException", "切断を返信待ちの打ち切りとして扱わない");
        opend.InitConnectCalls.Should().Be(1, "再接続しない");
        opend.QuotaRequests.Should().ContainSingle("切断の後は撃たない");
        opend.KLineRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task 切断の後の要求は再接続も送信もせずに失敗する()
    {
        // PR #1119 の監査 F2: 同じ接続オブジェクトへ InitConnect を再び呼ばない（接続は作り直さない＝コメントと一致）。
        var opend = new FakeOpenD();
        using var client = new MMApiMoomooKLineProbeClient(
            MoomooBrokerOptions.FromConfiguration(Config()), new Factory(opend), TimeSpan.FromSeconds(5));
        var first = await client.QueryQuotaAsync(false, TestContext.Current.CancellationToken);
        first.Succeeded.Should().BeTrue();

        client.OnDisconnect(new MMAPI_Conn(), 1);
        var act = () => client.QueryQuotaAsync(false, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*接続が切れた*");
        opend.InitConnectCalls.Should().Be(1, "切断の後に InitConnect を呼び直さない");
        opend.QuotaRequests.Should().ContainSingle("切断の後は撃たない");
    }

    [Fact]
    public void MMAPIの初期化は何度呼んでも1回だけである()
    {
        // PR #1119 の監査 F3: MoomooApi.EnsureInitialized の二重化防止（発注の接続と検証口の相場の接続が共有する）。
        var calls = 0;
        var initializer = new OnceInitializer(() => Interlocked.Increment(ref calls));

        initializer.Ensure();
        initializer.Ensure();
        Parallel.For(0, 16, _ => initializer.Ensure());

        calls.Should().Be(1);
    }

    [Fact]
    public async Task RSA鍵の内容とパスを出力に載せず鍵で暗号化して繋ぐ()
    {
        var keyPath = Path.Combine(Path.GetTempPath(), $"kline-probe-{Guid.NewGuid():N}.pem");
        await File.WriteAllTextAsync(keyPath, PemMarker, TestContext.Current.CancellationToken);
        try
        {
            var opend = new FakeOpenD();

            var (exitCode, output) = await Probe(opend, keyPath);

            exitCode.Should().Be(0, output);
            opend.RsaKeysApplied.Should().Equal(PemMarker);
            opend.Encrypt.Should().BeTrue("発注の接続と同じ RSA 鍵で暗号化して繋ぐ");
            output.Should().NotContain(PemMarker).And.NotContain(keyPath);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public async Task RSA鍵のパスが無いときの構成不正でもパスを出さず接続しない()
    {
        const string missingPath = "/run/kline-probe-test-dir/moomoo-rsa-7c1.pem";
        var opend = new FakeOpenD();

        var (exitCode, output) = await Probe(opend, missingPath);

        exitCode.Should().Be(KLineQuotaProbeCommand.ExitUsageOrConfiguration, output);
        opend.Created.Should().BeFalse("preflight で止まり接続オブジェクトを作らない");
        output.Should().Contain("result=config-error").And.Contain("RsaPrivateKeyPath '<伏せ>'");
        output.Should().NotContain(missingPath).And.NotContain("moomoo-rsa-7c1");
    }

    [Theory]
    [InlineData("paper", "sim")]
    [InlineData("moomoo", "live")]
    public void moomooのSIMULATE階層以外では照会口を組まない(string provider, string environment)
    {
        var opend = new FakeOpenD();

        var act = () => KLineQuotaProbeComposition.CreateQuery(Config(null, provider, environment), new Factory(opend));

        act.Should().Throw<InvalidOperationException>();
        opend.Created.Should().BeFalse("接続オブジェクトも作らない");
    }

    [Fact]
    public void 照会口の実体は相場だけのクライアントで発注の面を持たない()
    {
        // 🔴 構造で固定する: 実装が MMSPI_Trd・発注のポート・注文費用照会のポートを持つと落ちる。
        var opend = new FakeOpenD();
        using var query = (IDisposable)KLineQuotaProbeComposition.CreateQuery(Config(), new Factory(opend));

        query.Should().BeOfType<MMApiMoomooKLineProbeClient>();
        var interfaces = typeof(MMApiMoomooKLineProbeClient).GetInterfaces().Select(i => i.Name).ToList();
        interfaces.Should().NotContain(["MMSPI_Trd", "IMoomooTradeClient", "IBrokerAdapter", "IOrderFeeQuery", "IShortPermitSource"]);
        typeof(MMApiMoomooKLineProbeClient).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Select(f => f.FieldType.Name)
            .Should().NotContain(["IMoomooTradeConnection", "MMAPI_Trd", "IMoomooTradeClient"]);

        // 接続のシームの面は相場の 2 要求と接続の管理だけ（発注・口座の要求を足すと落ちる）。
        typeof(IMoomooQotProbeConnection).GetMethods().Select(m => m.Name).Should().BeEquivalentTo(
            "SetClientInfo", "SetConnCallback", "SetQotCallback", "SetRsaPrivateKey", "InitConnect",
            "RequestHistoryKL", "RequestHistoryKLQuota", "Close");
    }

    [Fact]
    public void 構成由来の伏せる値は注文費用照会の検証口と同じ導出である()
    {
        KLineQuotaProbeComposition.SensitiveValues(Config("/run/k/rsa.pem"))
            .Should().BeEquivalentTo([$"{ProbeHost}:{ProbePort}", ProbeHost, "/run/k/rsa.pem"]);
    }

    private sealed class FixedTimeProvider(DateOnly today) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
    }

    private sealed class Factory(FakeOpenD opend) : IMoomooQotProbeConnectionFactory
    {
        public IMoomooQotProbeConnection Create()
        {
            opend.Created = true;
            return opend;
        }
    }

    // 偽の OpenD。応答は実 SDK と同じく別スレッドから返す。取得枠は銘柄単位で消費する（前から AAPL を持つ）。
    private sealed class FakeOpenD : IMoomooQotProbeConnection
    {
        private readonly MMAPI_Conn _handle = new();
        private readonly HashSet<string> _consumed = ["AAPL"];
        private MMSPI_Conn? _conn;
        private MMSPI_Qot? _qot;
        private uint _serial;

        public bool Created { get; set; }

        public bool InitConnectFails { get; init; }

        public bool NeverReply { get; init; }

        // K 線の要求に返信せず、別スレッドから切断を通知する（応答待ちの間の切断）。
        public bool DisconnectOnKLine { get; init; }

        public int KLineRetType { get; init; }

        public string KLineRetMsg { get; init; } = "";

        public int InitConnectCalls { get; private set; }

        public bool Encrypt { get; private set; }

        public List<string> RsaKeysApplied { get; } = [];

        public List<QotRequestHistoryKL.Request> KLineRequests { get; } = [];

        public List<QotRequestHistoryKLQuota.Request> QuotaRequests { get; } = [];

        public void SetClientInfo(string clientId, int clientVersion) { }

        public void SetConnCallback(MMSPI_Conn callback) => _conn = callback;

        public void SetQotCallback(MMSPI_Qot callback) => _qot = callback;

        public void SetRsaPrivateKey(string privateKeyPem) => RsaKeysApplied.Add(privateKeyPem);

        public bool InitConnect(string host, ushort port, bool encrypt)
        {
            InitConnectCalls++;
            Encrypt = encrypt;
            if (InitConnectFails)
                return false;
            _ = Task.Run(() => _conn?.OnInitConnect(_handle, 0, string.Empty));
            return true;
        }

        public uint RequestHistoryKL(QotRequestHistoryKL.Request request)
        {
            KLineRequests.Add(request);
            var serial = ++_serial;
            if (NeverReply)
                return serial;
            if (DisconnectOnKLine)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(50);
                    _conn?.OnDisconnect(_handle, 1);
                });
                return serial;
            }
            var code = request.C2S.Security.Code;
            if (KLineRetType == 0)
                _consumed.Add(code);
            var s2c = QotRequestHistoryKL.S2C.CreateBuilder()
                .SetSecurity(request.C2S.Security)
                .AddKlList(QotCommon.KLine.CreateBuilder().SetTime("2026-09-26 00:00:00").SetIsBlank(false)
                    .SetOpenPrice(250).SetHighPrice(252).SetLowPrice(249).SetClosePrice(251).SetVolume(30000000).SetTurnover(7.5e9).BuildPartial())
                .AddKlList(QotCommon.KLine.CreateBuilder().SetTime("2026-09-28 00:00:00").SetIsBlank(true).BuildPartial())
                .AddKlList(QotCommon.KLine.CreateBuilder().SetTime("2026-09-29 00:00:00").SetIsBlank(false)
                    .SetOpenPrice(254.1).SetHighPrice(256.5).SetLowPrice(253).SetClosePrice(255.75).SetVolume(41234567).SetTurnover(10546789012.5).BuildPartial())
                .SetNextReqKey(Google.ProtocolBuffers.ByteString.CopyFrom([1, 2, 3]));
            var rsp = QotRequestHistoryKL.Response.CreateBuilder()
                .SetRetType(KLineRetType).SetRetMsg(KLineRetMsg).SetS2C(s2c.BuildPartial()).BuildPartial();
            _ = Task.Run(() => _qot?.OnReply_RequestHistoryKL(_handle, serial, rsp));
            return serial;
        }

        public uint RequestHistoryKLQuota(QotRequestHistoryKLQuota.Request request)
        {
            QuotaRequests.Add(request);
            var serial = ++_serial;
            if (NeverReply)
                return serial;
            var used = _consumed.Count + 6;
            var s2c = QotRequestHistoryKLQuota.S2C.CreateBuilder().SetUsedQuota(used).SetRemainQuota(300 - used);
            if (request.C2S.BGetDetail)
            {
                s2c.AddDetailList(QotRequestHistoryKLQuota.DetailItem.CreateBuilder()
                    .SetSecurity(QotCommon.Security.CreateBuilder().SetMarket((int)QotCommon.QotMarket.QotMarket_US_Security).SetCode("AAPL").Build())
                    .SetName("Apple").SetRequestTime("2026-09-29 22:10:05").SetRequestTimeStamp(1790719805).BuildPartial());
            }
            var rsp = QotRequestHistoryKLQuota.Response.CreateBuilder().SetRetType(0).SetRetMsg("").SetS2C(s2c.BuildPartial()).BuildPartial();
            _ = Task.Run(() => _qot?.OnReply_RequestHistoryKLQuota(_handle, serial, rsp));
            return serial;
        }

        public void Close() { }

        public void Dispose() { }
    }
}
