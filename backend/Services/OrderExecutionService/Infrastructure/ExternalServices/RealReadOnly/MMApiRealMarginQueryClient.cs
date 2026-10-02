using System.Collections.Concurrent;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using Moomoo.OpenApi;
using Moomoo.OpenApi.Pb;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution.QueryShortPermit;

namespace OrderExecutionService.Infrastructure.ExternalServices.RealReadOnly;

// FR-10, UC-06, ADR-0016 決定3（2026-08-06 追記）, ADR-0019 PoC 項目3, #1000, IADR-0144 決定3, IADR-0482:
// **実弾口座（Real × Margin）のヘッダで、借株可否と維持率の束（TrdGetMarginRatio）だけを照会する読み取り専用のクライアント。**
//
// なぜ要るか: TrdGetMarginRatio は SIMULATE 口座では `Get Margin Trading Data does not support Stocks in US Market` で失敗し、
// 実弾口座のヘッダでのみ成功する（IADR-0144 決定3 の実測）。計画 ADR-0016 決定3 の 2026-08-06 追記は
// 「SIMULATE で発注しながら実弾口座のヘッダで照会する、環境をまたぐ構成」を前提にせよと定め、裁定（#1000・2026-10-02）は
// 「読み取り専用で足す。発注系は一切作らない。発注経路から構造的に切り離す」とした。
//
// 🔴 **構造的な切り離し（IADR-0482 決定2）**:
//   - 本型が実装するのは IShortPermitSource（照会ポート）と SDK のコールバックだけである。IMoomooTradeClient・IBrokerAdapter・
//     IOrderAmendmentBroker 等、発注・訂正・取消の型を実装しない（試験が型の上で固定する）。
//   - 接続は自前の狭いシーム IMoomooMarginQueryConnection（口座一覧と TrdGetMarginRatio の 2 つだけ）で持ち、発注の面を持つ
//     IMoomooTradeConnection も、発注に使う接続オブジェクトも共有しない（OpenD への TCP 接続は別に張る）。
//   - 取引の解錠（UnlockTrade）を送らない。moomoo は実弾口座の発注に解錠を要するため、OpenD 側でも発注は通らない（二重の守り）。
//   - DI では IShortPermitSource としてだけ登録し、発注経路（アダプタ・予約の照会・建玉の照会）の誰も本型を受け取らない。
//   - 実弾の閂（LiveTradingGate・起動時拒否）は変えない。本型は閂の外側で、発注を SIMULATE に保ったまま照会だけを行う。
//
// 監査（IADR-0482 決定4）: 照会を送ったら答えの成否に関わらず RealAccountReadOnlyQueried を 1 件出す（取引環境 Real・
// 口座は末尾 2 桁以外を伏せる）。**監査に残せなければ答えを使わない**（例外を投げる＝呼び手は「分からない」）。
// ログ・例外の口座 ID は発注クライアントと同じ伏せ方（MoomooAccountIdRedaction。IADR-0473 / IADR-0476）。
public sealed class MMApiRealMarginQueryClient : MMSPI_Trd, MMSPI_Conn, IShortPermitSource, IDisposable
{
    public const string MarginRatioOperation = "GetMarginRatio";

    private readonly MoomooBrokerOptions _options;
    private readonly IRealReadOnlyQueryAudit _audit;
    private readonly IClock _clock;
    private readonly ILogger<MMApiRealMarginQueryClient> _logger;
    private readonly IMoomooMarginQueryConnectionFactory _connectionFactory;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<object>> _pending = new();
    private readonly object _sendGate = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly bool _encrypt;
    // RSA 秘密鍵（PKCS#1 PEM の内容）。**ログへ出さない。**
    private readonly string? _rsaPrivateKeyPem;

    private volatile IMoomooMarginQueryConnection _connection;
    private TaskCompletionSource<long>? _connectTcs;
    private volatile bool _connected;
    private volatile bool _connectionStale;
    private ulong _realAccId;
    // 口座一覧（userID=0＝ログイン中のユーザーの全口座）で見た口座 ID の集合（retMsg を伏せる値。減らさない）。
    private ulong[] _knownAccountIds = [];
    private readonly object _knownAccountIdsGate = new();
    private bool _disposed;

    public MMApiRealMarginQueryClient(
        MoomooBrokerOptions options,
        IRealReadOnlyQueryAudit audit,
        IClock clock,
        ILogger<MMApiRealMarginQueryClient> logger,
        IMoomooMarginQueryConnectionFactory? connectionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        MoomooPreflight.Validate(options, File.Exists);
        _options = options;
        _audit = audit;
        _clock = clock;
        _logger = logger;
        _connectionFactory = connectionFactory ?? new MMApiMarginQueryConnectionFactory();
        MoomooApi.EnsureInitialized();
        if (!string.IsNullOrWhiteSpace(options.RsaPrivateKeyPath))
        {
            _rsaPrivateKeyPem = File.ReadAllText(options.RsaPrivateKeyPath);
            _encrypt = true;
        }
        _connection = CreateConfiguredConnection();
    }

    // ---- IShortPermitSource（契約: 当該銘柄の行・欄が無ければ null、照会の失敗は例外）----

    public async Task<bool?> GetShortPermitAsync(string symbol, Market market, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        // 接続・口座の選択の失敗は、実弾のヘッダを 1 度も送っていない（監査の対象外。例外＝呼び手は「分からない」）。
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var accId = _realAccId;
        var queriedAt = _clock.UtcNow;
        bool? permit;
        try
        {
            permit = await QueryMarginRatioAsync(accId, symbol, market, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 送った（または送ったか分からない）照会も実弾のヘッダが出た事実である。失敗として残してから投げ直す。
            await RecordAsync(accId, symbol, market, RealAccountReadOnlyQueryOutcomes.Failed, queriedAt).ConfigureAwait(false);
            throw;
        }

        var outcome = permit switch
        {
            true => RealAccountReadOnlyQueryOutcomes.Permitted,
            false => RealAccountReadOnlyQueryOutcomes.NotPermitted,
            null => RealAccountReadOnlyQueryOutcomes.FieldMissing,
        };
        await RecordAsync(accId, symbol, market, outcome, queriedAt).ConfigureAwait(false);
        return permit;
    }

    private async Task<bool?> QueryMarginRatioAsync(ulong accId, string symbol, Market market, CancellationToken cancellationToken)
    {
        var (trdMarket, qotMarket) = MapMarket(market);
        var security = QotCommon.Security.CreateBuilder().SetMarket(qotMarket).SetCode(symbol).Build();
        var c2s = TrdGetMarginRatio.C2S.CreateBuilder()
            .SetHeader(BuildRealHeader(accId, trdMarket))
            .AddSecurityList(security)
            .Build();
        var req = TrdGetMarginRatio.Request.CreateBuilder().SetC2S(c2s).Build();
        var connection = _connection;
        var rsp = (TrdGetMarginRatio.Response)await SendAsync(() => connection.GetMarginRatio(req), cancellationToken)
            .ConfigureAwait(false);
        EnsureSucceeded(rsp.RetType, rsp.RetMsg, MarginRatioOperation);

        // 当該銘柄の行が無い・欄が載っていない、は「分からない」（null）。false（不許可）と取り違えない。
        var row = rsp.S2C.MarginRatioInfoListList
            .FirstOrDefault(info => string.Equals(info.Security.Code, symbol, StringComparison.OrdinalIgnoreCase));
        if (row is null || !row.HasIsShortPermit)
        {
            _logger.LogWarning(
                "実弾口座の読み取り専用の照会の応答に当該銘柄の行または IsShortPermit の欄がありません symbol={Symbol}。借株可否は不明として扱います。",
                symbol);
            return null;
        }
        return row.IsShortPermit;
    }

    private Task RecordAsync(ulong accId, string symbol, Market market, string outcome, DateTimeOffset queriedAt) =>
        _audit.RecordAsync(new RealAccountReadOnlyQueried(
            RealAccountReadOnlyQueried.RealTradingEnvironment,
            MarginRatioOperation,
            MoomooAccountIdRedaction.TailOnly(accId),
            symbol,
            market,
            outcome,
            queriedAt));

    // 🔴 **実弾のヘッダを作るのは本メソッドだけである。** 照会（TrdGetMarginRatio）の C2S にしか渡さない。
    private static TrdCommon.TrdHeader BuildRealHeader(ulong accId, int trdMarket) =>
        TrdCommon.TrdHeader.CreateBuilder()
            .SetTrdEnv((int)TrdCommon.TrdEnv.TrdEnv_Real)
            .SetAccID(accId)
            .SetTrdMarket(trdMarket)
            .Build();

    // 空売りの照会対象は米国株だけ（ADR-0016 決定13。呼び手の ShortPermitQueryService が米国株以外を弾く）。
    private static (int TrdMarket, int QotMarket) MapMarket(Market market) => market switch
    {
        Market.UnitedStates => ((int)TrdCommon.TrdMarket.TrdMarket_US, (int)QotCommon.QotMarket.QotMarket_US_Security),
        _ => throw new NotSupportedException($"実弾口座の読み取り専用の照会は米国株だけを扱います（market={market}）。"),
    };

    // ---- 接続・口座 ----

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_connected)
            return;
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connected)
                return;
            if (_connectionStale)
                RecreateConnection();
            _connectTcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            _logger.LogInformation(
                "OpenD へ接続します（実弾口座の読み取り専用の照会用・発注はしない） {Host}:{Port} encrypt={Encrypt}",
                _options.OpenDHost, _options.OpenDPort, _encrypt);
            if (!_connection.InitConnect(_options.OpenDHost, _options.OpenDPort, _encrypt))
                throw new InvalidOperationException(
                    $"OpenD への InitConnect が失敗しました（実弾口座の読み取り専用の照会用・{_options.OpenDHost}:{_options.OpenDPort}）。");
            await _connectTcs.Task.WaitAsync(_options.ReplyTimeout, cancellationToken).ConfigureAwait(false);
            _realAccId = await FetchRealMarginAccountAsync(cancellationToken).ConfigureAwait(false);
            _connected = true;
            _logger.LogInformation(
                "実弾口座の読み取り専用の照会の接続完了 accId={AccId}（照会は TrdGetMarginRatio だけ・発注はしない）",
                MoomooAccountIdRedaction.TailOnly(_realAccId));
        }
        finally
        {
            if (!_connected)
                _connectionStale = true;
            _connectTcs = null;
            _connectGate.Release();
        }
    }

    // 口座一覧（TrdGetAccList）から**実弾（TrdEnv_Real）× 信用（TrdAccType_Margin）× 米国株の取扱あり**を 1 つだけ選ぶ。
    // 該当が無い・2 つ以上ある、はどちらも選ばない（例外＝照会しない＝「分からない」）。取り違えた口座で照会しない。
    private async Task<ulong> FetchRealMarginAccountAsync(CancellationToken cancellationToken)
    {
        var c2s = TrdGetAccList.C2S.CreateBuilder().SetUserID(0).Build();
        var req = TrdGetAccList.Request.CreateBuilder().SetC2S(c2s).Build();
        var connection = _connection;
        var rsp = (TrdGetAccList.Response)await SendAsync(() => connection.GetAccList(req), cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(rsp.RetType, rsp.RetMsg, "GetAccList");
        RememberAccountIds(rsp.S2C.AccListList);

        var candidates = rsp.S2C.AccListList
            .Where(IsRealMarginUs)
            .Select(acc => acc.AccID)
            .Distinct()
            .ToList();
        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new InvalidOperationException(
                "OpenD の口座一覧に実弾の信用口座（Real × Margin・米国株の取扱あり）が見つかりません。借株可否は照会しません。"),
            _ => throw new InvalidOperationException(
                $"OpenD の口座一覧に実弾の信用口座（Real × Margin・米国株の取扱あり）が {candidates.Count} 件あります。"
                + "どれで照会するか決められないため照会しません。"),
        };
    }

    public static bool IsRealMarginUs(TrdCommon.TrdAcc acc)
    {
        ArgumentNullException.ThrowIfNull(acc);
        return acc.TrdEnv == (int)TrdCommon.TrdEnv.TrdEnv_Real
            && acc.AccType == (int)TrdCommon.TrdAccType.TrdAccType_Margin
            && acc.AccID != 0
            && acc.TrdMarketAuthListList.Contains((int)TrdCommon.TrdMarket.TrdMarket_US);
    }

    private IMoomooMarginQueryConnection CreateConfiguredConnection()
    {
        var connection = _connectionFactory.Create();
        connection.SetClientInfo("ai-stock-trading-real-readonly", 1);
        connection.SetConnCallback(this);
        connection.SetTrdCallback(this);
        if (_rsaPrivateKeyPem is not null)
            connection.SetRsaPrivateKey(_rsaPrivateKeyPem);
        return connection;
    }

    private void RecreateConnection()
    {
        var stale = _connection;
        _connection = CreateConfiguredConnection();
        try
        {
            stale.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "実弾口座の読み取り専用の照会の OpenD 接続オブジェクトの Close で例外（解放は続行します）");
        }
        finally
        {
            try
            {
                stale.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "実弾口座の読み取り専用の照会の OpenD 接続オブジェクトの Dispose で例外");
            }
        }
        _connectionStale = false;
    }

    // ---- 応答相関 ----

    private Task<object> SendAsync(Func<uint> send, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sendGate)
        {
            var serial = send();
            _pending[serial] = tcs;
        }
        return tcs.Task.WaitAsync(_options.ReplyTimeout, cancellationToken);
    }

    private void Complete(uint serial, object rsp)
    {
        TaskCompletionSource<object>? tcs;
        lock (_sendGate)
        {
            _pending.TryRemove(serial, out tcs);
        }
        tcs?.TrySetResult(rsp);
    }

    // 非成功は retType / retMsg を保つ例外で投げる。retMsg の口座 ID は作る時点で伏せる（IADR-0476 と同じ）。
    private void EnsureSucceeded(int retType, string retMsg, string op)
    {
        if (retType != 0)
        {
            throw new MoomooTradeRequestException(
                op, retType, MoomooAccountIdRedaction.RedactRetMsg(retMsg, Volatile.Read(ref _knownAccountIds)));
        }
    }

    private void RememberAccountIds(IEnumerable<TrdCommon.TrdAcc> accounts)
    {
        lock (_knownAccountIdsGate)
        {
            var merged = new HashSet<ulong>(_knownAccountIds);
            foreach (var acc in accounts)
            {
                if (acc.AccID != 0)
                    merged.Add(acc.AccID);
            }
            if (merged.Count != _knownAccountIds.Length)
                Volatile.Write(ref _knownAccountIds, merged.ToArray());
        }
    }

    // ---- MMSPI_Conn ----

    public void OnInitConnect(MMAPI_Conn client, long errCode, string desc)
    {
        var tcs = _connectTcs;
        if (errCode == 0)
        {
            tcs?.TrySetResult(errCode);
        }
        else
        {
            _logger.LogError("OpenD 接続失敗（実弾口座の読み取り専用の照会用） errCode={ErrCode} desc={Desc}", errCode, desc);
            tcs?.TrySetException(new InvalidOperationException($"OpenD 接続失敗 errCode={errCode}: {desc}"));
        }
    }

    public void OnDisconnect(MMAPI_Conn client, long errCode)
    {
        _connected = false;
        _connectionStale = true;
        _logger.LogWarning("OpenD 切断（実弾口座の読み取り専用の照会用） errCode={ErrCode}", errCode);
    }

    // ---- MMSPI_Trd（使用する照会のコールバック）----

    public void OnReply_GetAccList(MMAPI_Conn client, uint nSerialNo, TrdGetAccList.Response rsp) => Complete(nSerialNo, rsp);

    public void OnReply_GetMarginRatio(MMAPI_Conn client, uint nSerialNo, TrdGetMarginRatio.Response rsp) => Complete(nSerialNo, rsp);

    // ---- MMSPI_Trd（SDK のコールバック面が要求するもの。本型は要求を送らないため常に no-op）----

    public void OnReply_UnlockTrade(MMAPI_Conn client, uint nSerialNo, TrdUnlockTrade.Response rsp) { }
    public void OnReply_SubAccPush(MMAPI_Conn client, uint nSerialNo, TrdSubAccPush.Response rsp) { }
    public void OnReply_GetFunds(MMAPI_Conn client, uint nSerialNo, TrdGetFunds.Response rsp) { }
    public void OnReply_GetPositionList(MMAPI_Conn client, uint nSerialNo, TrdGetPositionList.Response rsp) { }
    public void OnReply_GetMaxTrdQtys(MMAPI_Conn client, uint nSerialNo, TrdGetMaxTrdQtys.Response rsp) { }
    public void OnReply_GetComboMaxTrdQtys(MMAPI_Conn client, uint nSerialNo, TrdGetComboMaxTrdQtys.Response rsp) { }
    public void OnReply_GetOrderList(MMAPI_Conn client, uint nSerialNo, TrdGetOrderList.Response rsp) { }
    public void OnReply_PlaceOrder(MMAPI_Conn client, uint nSerialNo, TrdPlaceOrder.Response rsp) { }
    public void OnReply_ModifyOrder(MMAPI_Conn client, uint nSerialNo, TrdModifyOrder.Response rsp) { }
    public void OnReply_UpdateOrder(MMAPI_Conn client, uint nSerialNo, TrdUpdateOrder.Response rsp) { }
    public void OnReply_GetOrderFillList(MMAPI_Conn client, uint nSerialNo, TrdGetOrderFillList.Response rsp) { }
    public void OnReply_UpdateOrderFill(MMAPI_Conn client, uint nSerialNo, TrdUpdateOrderFill.Response rsp) { }
    public void OnReply_GetHistoryOrderList(MMAPI_Conn client, uint nSerialNo, TrdGetHistoryOrderList.Response rsp) { }
    public void OnReply_GetHistoryOrderFillList(MMAPI_Conn client, uint nSerialNo, TrdGetHistoryOrderFillList.Response rsp) { }
    public void OnReply_GetOrderFee(MMAPI_Conn client, uint nSerialNo, TrdGetOrderFee.Response rsp) { }
    public void OnReply_GetFlowSummary(MMAPI_Conn client, uint nSerialNo, TrdFlowSummary.Response rsp) { }
    public void OnReply_PlaceComboOrder(MMAPI_Conn client, uint nSerialNo, TrdPlaceComboOrder.Response rsp) { }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _connection.Close();
            _connection.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "実弾口座の読み取り専用の照会の OpenD クライアントの解放中に例外");
        }
        _connectGate.Dispose();
    }
}
