using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Moomoo.OpenApi;
using Moomoo.OpenApi.Pb;

namespace AiStockTrading.OrderExecution.Worker.Composable.Adapters;

// #13, FR-05, ADR-0002, IADR-0016: moomoo-api（MMAPI4Net）による実 OpenD 結合。
// SIMULATE 限定（TrdEnv_Simulate=0）で発注する。実弾（TrdEnv_Real）は撃たない。
//
// OpenD（常駐・#124）へ TCP protobuf で接続し、非同期コールバック（nSerialNo 相関）で応答を待つ。
// MMSPI_Conn（接続）と MMSPI_Trd（取引・全 OnReply_* 実装が必要）の両インターフェースを実装する。
// 未使用のコールバックは no-op。接続/口座取得は初回利用時に遅延実行する（起動をブロックしない）。
internal sealed class MMApiMoomooTradeClient : MMSPI_Trd, MMSPI_Conn, IMoomooTradeClient, IDisposable
{
    private static readonly object InitGate = new();
    private static bool _apiInitialized;
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(15);

    private readonly MoomooBrokerOptions _options;
    private readonly ILogger<MMApiMoomooTradeClient> _logger;
    private readonly MMAPI_Trd _trd = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<object>> _pending = new();
    // 照会/取消は市場（TrdMarket/TrdSecMarket）を要するため、発注時に orderId→市場を控える。
    private readonly ConcurrentDictionary<string, (int TrdMarket, int SecMarket)> _orderMarket = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    private TaskCompletionSource<long>? _connectTcs;
    private volatile bool _connected;
    private readonly bool _encrypt;
    private ulong _simAccId;
    private bool _disposed;

    public MMApiMoomooTradeClient(MoomooBrokerOptions options, ILogger<MMApiMoomooTradeClient> logger)
    {
        _options = options;
        _logger = logger;
        lock (InitGate)
        {
            if (!_apiInitialized)
            {
                MMAPI.Init();
                _apiInitialized = true;
            }
        }
        _trd.SetClientInfo("ai-stock-trading", 1);
        _trd.SetConnCallback(this);
        _trd.SetTrdCallback(this);
        // moomoo は cross-network の trade 接続に暗号化を要求する。RSA 秘密鍵が構成されていれば暗号化で接続する。
        // SetRSAPrivateKey は鍵の内容（PKCS#1 PEM 文字列）を受け取る（パスではない）。
        if (!string.IsNullOrWhiteSpace(options.RsaPrivateKeyPath) && File.Exists(options.RsaPrivateKeyPath))
        {
            _trd.SetRSAPrivateKey(File.ReadAllText(options.RsaPrivateKeyPath));
            _encrypt = true;
        }
    }

    // ---- IMoomooTradeClient ----

    public async Task<MoomooOrderResult> PlaceOrderAsync(MoomooOrderRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var (trdMarket, secMarket) = MapMarket(request.Market);
        var side = request.Side == MoomooSide.Sell ? TrdCommon.TrdSide.TrdSide_Sell : TrdCommon.TrdSide.TrdSide_Buy;

        var c2s = TrdPlaceOrder.C2S.CreateBuilder()
            .SetPacketID(_trd.NextPacketID()) // 発注は packetID（冪等キー）必須
            .SetHeader(BuildHeader(trdMarket))
            .SetTrdSide((int)side)
            .SetOrderType((int)TrdCommon.OrderType.OrderType_Normal) // 指値
            .SetCode(request.Symbol)
            .SetQty(request.Quantity)
            .SetPrice((double)request.Price)
            .SetSecMarket(secMarket)
            .Build();
        var req = TrdPlaceOrder.Request.CreateBuilder().SetC2S(c2s).Build();

        var rsp = (TrdPlaceOrder.Response)await SendAsync(() => _trd.PlaceOrder(req), cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(rsp.RetType, rsp.RetMsg, "PlaceOrder");

        var orderId = rsp.S2C.OrderID.ToString();
        _orderMarket[orderId] = (trdMarket, secMarket);
        _logger.LogInformation("moomoo SIMULATE 発注成功 orderId={OrderId} {Side} {Symbol} x{Qty}@{Price}",
            orderId, side, request.Symbol, request.Quantity, request.Price);
        // 発注直後は約定前。状態追跡は QueryOrderAsync（GetOrderList）で行う。
        return new MoomooOrderResult(orderId, MoomooOrderState.Submitted, 0, 0m);
    }

    public async Task<MoomooOrderResult?> QueryOrderAsync(string orderId, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        if (!ulong.TryParse(orderId, out var oid))
        {
            return null;
        }
        var (trdMarket, _) = MarketFor(orderId);
        var c2s = TrdGetOrderList.C2S.CreateBuilder()
            .SetHeader(BuildHeader(trdMarket))
            .SetRefreshCache(true)
            .Build();
        var req = TrdGetOrderList.Request.CreateBuilder().SetC2S(c2s).Build();

        var rsp = (TrdGetOrderList.Response)await SendAsync(() => _trd.GetOrderList(req), cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(rsp.RetType, rsp.RetMsg, "GetOrderList");

        foreach (TrdCommon.Order o in rsp.S2C.OrderListList)
        {
            if (o.OrderID == oid)
            {
                return new MoomooOrderResult(orderId, MapState(o.OrderStatus), (int)o.FillQty, (decimal)o.FillAvgPrice);
            }
        }
        return null;
    }

    public async Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        if (!ulong.TryParse(orderId, out var oid))
        {
            return;
        }
        var (trdMarket, _) = MarketFor(orderId);
        var c2s = TrdModifyOrder.C2S.CreateBuilder()
            .SetPacketID(_trd.NextPacketID()) // 変更/取消も packetID 必須
            .SetHeader(BuildHeader(trdMarket))
            .SetOrderID(oid)
            .SetModifyOrderOp((int)TrdCommon.ModifyOrderOp.ModifyOrderOp_Cancel)
            .Build();
        var req = TrdModifyOrder.Request.CreateBuilder().SetC2S(c2s).Build();

        var rsp = (TrdModifyOrder.Response)await SendAsync(() => _trd.ModifyOrder(req), cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(rsp.RetType, rsp.RetMsg, "CancelOrder");
    }

    // ---- 接続・口座 ----

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_connected)
        {
            return;
        }
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connected)
            {
                return;
            }
            _connectTcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            _logger.LogInformation("OpenD へ接続します {Host}:{Port} encrypt={Encrypt}", _options.OpenDHost, _options.OpenDPort, _encrypt);
            if (!_trd.InitConnect(_options.OpenDHost, _options.OpenDPort, _encrypt))
            {
                throw new InvalidOperationException($"OpenD への InitConnect が失敗しました（{_options.OpenDHost}:{_options.OpenDPort}）。");
            }
            await _connectTcs.Task.WaitAsync(ReplyTimeout, cancellationToken).ConfigureAwait(false);
            _simAccId = await FetchSimulateAccIdAsync(cancellationToken).ConfigureAwait(false);
            _connected = true;
            _logger.LogInformation("OpenD 接続完了・SIMULATE 口座 accId={AccId}", _simAccId);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task<ulong> FetchSimulateAccIdAsync(CancellationToken cancellationToken)
    {
        // userID は protobuf required。0 = 現在ログイン中のユーザー（全口座）。
        var c2s = TrdGetAccList.C2S.CreateBuilder().SetUserID(0).Build();
        var req = TrdGetAccList.Request.CreateBuilder().SetC2S(c2s).Build();
        var rsp = (TrdGetAccList.Response)await SendAsync(() => _trd.GetAccList(req), cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(rsp.RetType, rsp.RetMsg, "GetAccList");

        foreach (TrdCommon.TrdAcc acc in rsp.S2C.AccListList)
        {
            if (acc.TrdEnv == (int)TrdCommon.TrdEnv.TrdEnv_Simulate)
            {
                return acc.AccID;
            }
        }
        throw new InvalidOperationException("OpenD に SIMULATE 口座が見つかりません（moomoo の模擬取引口座を有効化してください）。");
    }

    private TrdCommon.TrdHeader BuildHeader(int trdMarket) =>
        TrdCommon.TrdHeader.CreateBuilder()
            .SetTrdEnv((int)TrdCommon.TrdEnv.TrdEnv_Simulate) // SIMULATE 固定（実弾を撃たない）
            .SetAccID(_simAccId)
            .SetTrdMarket(trdMarket)
            .Build();

    private (int TrdMarket, int SecMarket) MarketFor(string orderId) =>
        _orderMarket.TryGetValue(orderId, out var mk)
            ? mk
            : ((int)TrdCommon.TrdMarket.TrdMarket_US, (int)TrdCommon.TrdSecMarket.TrdSecMarket_US);

    private static (int TrdMarket, int SecMarket) MapMarket(MoomooMarket market) => market switch
    {
        MoomooMarket.Japan => ((int)TrdCommon.TrdMarket.TrdMarket_JP, (int)TrdCommon.TrdSecMarket.TrdSecMarket_JP),
        _ => ((int)TrdCommon.TrdMarket.TrdMarket_US, (int)TrdCommon.TrdSecMarket.TrdSecMarket_US),
    };

    // OpenD OrderStatus（TrdCommon.OrderStatus）を moomoo アダプタの状態へ写像する。
    private static MoomooOrderState MapState(int openDStatus) => openDStatus switch
    {
        0 or 1 or 2 => MoomooOrderState.Submitting,      // Unsubmitted / WaitingSubmit / Submitting
        5 => MoomooOrderState.Submitted,                 // Submitted
        10 => MoomooOrderState.FilledPart,               // Filled_Part
        11 => MoomooOrderState.FilledAll,                // Filled_All
        12 or 13 => MoomooOrderState.Submitted,          // Cancelling_*（取消進行中・まだ有効）
        14 or 15 or 24 => MoomooOrderState.Cancelled,    // Cancelled_Part / Cancelled_All / FillCancelled
        _ => MoomooOrderState.Failed,                    // SubmitFailed / TimeOut / Failed / Disabled / Deleted / Unknown
    };

    // ---- 応答相関 ----

    private Task<object> SendAsync(Func<uint> send, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serial = send();
        _pending[serial] = tcs;
        return tcs.Task.WaitAsync(ReplyTimeout, cancellationToken);
    }

    private void Complete(uint serial, object rsp)
    {
        if (_pending.TryRemove(serial, out var tcs))
        {
            tcs.TrySetResult(rsp);
        }
    }

    private static void EnsureSucceeded(int retType, string retMsg, string op)
    {
        if (retType != 0) // RetType_Succeed=0
        {
            throw new InvalidOperationException($"moomoo {op} が失敗しました（retType={retType}）: {retMsg}");
        }
    }

    // ---- MMSPI_Conn ----

    public void OnInitConnect(MMAPI_Conn client, long errCode, string desc)
    {
        var tcs = _connectTcs;
        if (errCode == 0)
        {
            _logger.LogInformation("OpenD 接続確立 connID={ConnId}", client.GetConnectID());
            tcs?.TrySetResult(errCode);
        }
        else
        {
            _logger.LogError("OpenD 接続失敗 errCode={ErrCode} desc={Desc}", errCode, desc);
            tcs?.TrySetException(new InvalidOperationException($"OpenD 接続失敗 errCode={errCode}: {desc}"));
        }
    }

    public void OnDisconnect(MMAPI_Conn client, long errCode)
    {
        _connected = false;
        _logger.LogWarning("OpenD 切断 errCode={ErrCode}", errCode);
    }

    // ---- MMSPI_Trd（使用するコールバック）----

    public void OnReply_GetAccList(MMAPI_Conn client, uint nSerialNo, TrdGetAccList.Response rsp) => Complete(nSerialNo, rsp);
    public void OnReply_PlaceOrder(MMAPI_Conn client, uint nSerialNo, TrdPlaceOrder.Response rsp) => Complete(nSerialNo, rsp);
    public void OnReply_GetOrderList(MMAPI_Conn client, uint nSerialNo, TrdGetOrderList.Response rsp) => Complete(nSerialNo, rsp);
    public void OnReply_ModifyOrder(MMAPI_Conn client, uint nSerialNo, TrdModifyOrder.Response rsp) => Complete(nSerialNo, rsp);

    // ---- MMSPI_Trd（未使用・no-op）----

    public void OnReply_UnlockTrade(MMAPI_Conn client, uint nSerialNo, TrdUnlockTrade.Response rsp) { }
    public void OnReply_SubAccPush(MMAPI_Conn client, uint nSerialNo, TrdSubAccPush.Response rsp) { }
    public void OnReply_GetFunds(MMAPI_Conn client, uint nSerialNo, TrdGetFunds.Response rsp) { }
    public void OnReply_GetPositionList(MMAPI_Conn client, uint nSerialNo, TrdGetPositionList.Response rsp) { }
    public void OnReply_GetMaxTrdQtys(MMAPI_Conn client, uint nSerialNo, TrdGetMaxTrdQtys.Response rsp) { }
    public void OnReply_GetComboMaxTrdQtys(MMAPI_Conn client, uint nSerialNo, TrdGetComboMaxTrdQtys.Response rsp) { }
    public void OnReply_GetOrderFillList(MMAPI_Conn client, uint nSerialNo, TrdGetOrderFillList.Response rsp) { }
    public void OnReply_GetHistoryOrderList(MMAPI_Conn client, uint nSerialNo, TrdGetHistoryOrderList.Response rsp) { }
    public void OnReply_GetHistoryOrderFillList(MMAPI_Conn client, uint nSerialNo, TrdGetHistoryOrderFillList.Response rsp) { }
    public void OnReply_GetMarginRatio(MMAPI_Conn client, uint nSerialNo, TrdGetMarginRatio.Response rsp) { }
    public void OnReply_GetOrderFee(MMAPI_Conn client, uint nSerialNo, TrdGetOrderFee.Response rsp) { }
    public void OnReply_GetFlowSummary(MMAPI_Conn client, uint nSerialNo, TrdFlowSummary.Response rsp) { }
    public void OnReply_PlaceComboOrder(MMAPI_Conn client, uint nSerialNo, TrdPlaceComboOrder.Response rsp) { }
    public void OnReply_UpdateOrder(MMAPI_Conn client, uint nSerialNo, TrdUpdateOrder.Response rsp) { }
    public void OnReply_UpdateOrderFill(MMAPI_Conn client, uint nSerialNo, TrdUpdateOrderFill.Response rsp) { }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _trd.Close();
            _trd.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenD クライアントの解放中に例外");
        }
        _connectGate.Dispose();
    }
}
