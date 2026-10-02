using AiStockTrading.Shared.Contracts.Events;
using Moomoo.OpenApi;
using Moomoo.OpenApi.Pb;
using OrderExecutionService.Infrastructure.ExternalServices.RealReadOnly;

namespace OrderExecutionService.Tests;

// #1000, IADR-0482: 実弾口座の読み取り専用の照会の試験に使う偽の OpenD（口座一覧と TrdGetMarginRatio だけに応える）。
internal sealed class FakeMarginQueryOpenD(
    IReadOnlyList<TrdCommon.TrdAcc> accounts,
    Func<TrdGetMarginRatio.Request, TrdGetMarginRatio.Response> reply)
    : IMoomooMarginQueryConnection, IMoomooMarginQueryConnectionFactory
{
    public const ulong SimAccId = 724808UL;
    public const ulong RealCashAccId = 281234567UL;
    public const ulong RealMarginAccId = 281234599UL;

    private readonly MMAPI_Conn _handle = new();
    private MMSPI_Conn? _connCallback;
    private MMSPI_Trd? _trdCallback;
    private uint _serial;

    public List<TrdGetMarginRatio.Request> Requests { get; } = [];

    public int AccListCalls { get; private set; }

    public IMoomooMarginQueryConnection Create() => this;

    // 口座一覧の既定: SIMULATE（発注口座）・実弾の現金口座・実弾の信用口座（米国株）の 3 つ。照会に使ってよいのは最後だけ。
    public static IReadOnlyList<TrdCommon.TrdAcc> DefaultAccounts() =>
    [
        Account(SimAccId, TrdCommon.TrdEnv.TrdEnv_Simulate, TrdCommon.TrdAccType.TrdAccType_Margin, TrdCommon.TrdMarket.TrdMarket_US),
        Account(RealCashAccId, TrdCommon.TrdEnv.TrdEnv_Real, TrdCommon.TrdAccType.TrdAccType_Cash, TrdCommon.TrdMarket.TrdMarket_US),
        Account(RealMarginAccId, TrdCommon.TrdEnv.TrdEnv_Real, TrdCommon.TrdAccType.TrdAccType_Margin, TrdCommon.TrdMarket.TrdMarket_US),
    ];

    public static TrdCommon.TrdAcc Account(
        ulong id, TrdCommon.TrdEnv env, TrdCommon.TrdAccType type, params TrdCommon.TrdMarket[] markets)
    {
        var builder = TrdCommon.TrdAcc.CreateBuilder().SetTrdEnv((int)env).SetAccID(id).SetAccType((int)type);
        foreach (var market in markets)
            builder.AddTrdMarketAuthList((int)market);
        return builder.BuildPartial();
    }

    public static TrdGetMarginRatio.Response Reply(int retType, string retMsg, params TrdGetMarginRatio.MarginRatioInfo[] rows)
    {
        var s2c = TrdGetMarginRatio.S2C.CreateBuilder();
        foreach (var row in rows)
            s2c.AddMarginRatioInfoList(row);
        return TrdGetMarginRatio.Response.CreateBuilder().SetRetType(retType).SetRetMsg(retMsg).SetS2C(s2c.BuildPartial()).BuildPartial();
    }

    public static TrdGetMarginRatio.MarginRatioInfo Row(string code, bool? permit)
    {
        var builder = TrdGetMarginRatio.MarginRatioInfo.CreateBuilder()
            .SetSecurity(QotCommon.Security.CreateBuilder()
                .SetMarket((int)QotCommon.QotMarket.QotMarket_US_Security).SetCode(code).Build());
        if (permit is { } p)
            builder.SetIsShortPermit(p);
        return builder.BuildPartial();
    }

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

    public uint GetAccList(TrdGetAccList.Request request)
    {
        var serial = Interlocked.Increment(ref _serial);
        AccListCalls++;
        var s2c = TrdGetAccList.S2C.CreateBuilder();
        foreach (var acc in accounts)
            s2c.AddAccList(acc);
        var response = TrdGetAccList.Response.CreateBuilder().SetRetType(0).SetRetMsg(string.Empty).SetS2C(s2c.BuildPartial()).BuildPartial();
        _ = Task.Run(() => _trdCallback?.OnReply_GetAccList(_handle, serial, response));
        return serial;
    }

    public uint GetMarginRatio(TrdGetMarginRatio.Request request)
    {
        var serial = Interlocked.Increment(ref _serial);
        Requests.Add(request);
        var response = reply(request);
        _ = Task.Run(() => _trdCallback?.OnReply_GetMarginRatio(_handle, serial, response));
        return serial;
    }

    public void Dispose() { }
}

// 監査の記録を手元に残す偽物（Fail=true なら記録に失敗する）。
internal sealed class RecordingRealReadOnlyQueryAudit : IRealReadOnlyQueryAudit
{
    private readonly List<RealAccountReadOnlyQueried> _entries = [];

    public bool Fail { get; set; }

    public IReadOnlyList<RealAccountReadOnlyQueried> Entries
    {
        get { lock (_entries) return _entries.ToArray(); }
    }

    public Task RecordAsync(RealAccountReadOnlyQueried entry)
    {
        if (Fail)
            throw new InvalidOperationException("監査の記録に失敗（試験）");
        lock (_entries) _entries.Add(entry);
        return Task.CompletedTask;
    }
}
