using System.Globalization;
using Google.ProtocolBuffers;
using Microsoft.Extensions.Logging;
using Moomoo.OpenApi;
using Moomoo.OpenApi.Pb;

namespace AiStockTrading.Backtest.Infrastructure.Composable.Adapters;

// FR-15, ADR-0023 決定5, IADR-0157, #382: moomoo-api（MMAPI4Net）による実 OpenD 結合。
// **米国株の日足 K 線（QotRequestHistoryKL）を 1 リクエスト分だけ取得する**（ページングの制御は
// MoomooHistoricalBarSource が持つ。SDK 依存を本クラス 1 つに閉じるため）。
//
// 発注経路（MMApiMoomooTradeClient・#13 / IADR-0016）と同じ形で書く: OpenD へ TCP protobuf で接続し、
// 非同期コールバック（nSerialNo 相関）で応答を待つ。接続は初回利用時に遅延実行する（起動をブロックしない）。
// **本クラスは相場（Qot）系のみを扱い、発注は一切行わない**（MMSPI_Trd を実装していない）。
//
// MMSPI_Qot は 133 のコールバックの実装を要するインターフェースである。使うのは
// OnReply_RequestHistoryKL の 1 つだけであり、残りは no-op として並べる（SDK の要求であって選択ではない）。
// **取得枠の照会（RequestHistoryKLQuota）は実装していない。** 枠の単位・回復周期の確認は実 OpenD を要する
// 未了の作業であり（ADR-0023 決定5 の確認事項 1・docs/blocked-tasks.md A-3）、推測で機構を足さない。
public sealed class MMApiMoomooHistoryKLineClient : MMSPI_Qot, MMSPI_Conn, IMoomooHistoryKLineClient, IDisposable
{
    private static readonly object InitGate = new();
    private static bool _apiInitialized;

    // ADR-0023 決定5: 日足（KLType_Day）で取得する。
    internal const int DayKLType = (int)QotCommon.KLType.KLType_Day;

    // ADR-0023 決定5: 米国株（QotMarket_US_Security）。本クラスは米国株のみを扱う。
    internal const int UsSecurityMarket = (int)QotCommon.QotMarket.QotMarket_US_Security;

    // OHLCV を要求する（High|Open|Low|Close|Volume）。要求しない項目は応答に載らない。
    internal const long OhlcvFields =
        (long)QotCommon.KLFields.KLFields_High | (long)QotCommon.KLFields.KLFields_Open |
        (long)QotCommon.KLFields.KLFields_Low | (long)QotCommon.KLFields.KLFields_Close |
        (long)QotCommon.KLFields.KLFields_Volume;

    private readonly MoomooBarDataOptions _options;
    private readonly TimeSpan _replyTimeout;
    private readonly ILogger<MMApiMoomooHistoryKLineClient> _logger;
    private readonly MMAPI_Qot _qot = new();
    private readonly Dictionary<uint, TaskCompletionSource<QotRequestHistoryKL.Response>> _pending = [];
    private readonly object _sendGate = new(); // serial 採番＋登録とコールバック完了の相互排他（レース防止）。
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly bool _encrypt;

    private TaskCompletionSource<long>? _connectTcs;
    private volatile bool _connected;
    private bool _disposed;

    public MMApiMoomooHistoryKLineClient(MoomooBarDataOptions options, ILogger<MMApiMoomooHistoryKLineClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _replyTimeout = TimeSpan.FromSeconds(options.ReplyTimeoutSeconds);
        _logger = logger;
        lock (InitGate)
        {
            if (!_apiInitialized)
            {
                MMAPI.Init();
                _apiInitialized = true;
            }
        }
        _qot.SetClientInfo("ai-stock-trading", 1);
        _qot.SetConnCallback(this);
        _qot.SetQotCallback(this);
        // 相場系は暗号化必須ではないが、OpenD 側が暗号化を要求する構成なら鍵を渡す（発注経路と同じ扱い）。
        if (!string.IsNullOrWhiteSpace(options.RsaPrivateKeyPath))
        {
            _qot.SetRSAPrivateKey(File.ReadAllText(options.RsaPrivateKeyPath));
            _encrypt = true;
        }
    }

    // ---- IMoomooHistoryKLineClient ----

    public async Task<MoomooHistoryKLinePage> RequestUsDailyKLinesAsync(
        MoomooHistoryKLineRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var security = QotCommon.Security.CreateBuilder()
            .SetMarket(UsSecurityMarket)
            .SetCode(request.Symbol)
            .Build();
        var c2s = QotRequestHistoryKL.C2S.CreateBuilder()
            .SetSecurity(security)
            .SetKlType(DayKLType)
            .SetRehabType(MapRehabType(request.Adjustment)) // ADR-0023 決定5: 前復権
            .SetBeginTime(FormatDate(request.From))
            .SetEndTime(FormatDate(request.To))
            .SetMaxAckKLNum(request.MaxCount)              // ADR-0023 決定5: 1 リクエスト 1,000 件上限
            .SetNeedKLFieldsFlag(OhlcvFields);
        // 続きの取得（ページング）。先頭ページでは付けない。
        if (request.NextRequestKey is { Length: > 0 } key)
            c2s.SetNextReqKey(ByteString.CopyFrom(key));

        var req = QotRequestHistoryKL.Request.CreateBuilder().SetC2S(c2s.Build()).Build();
        var rsp = await SendAsync(() => _qot.RequestHistoryKL(req), cancellationToken).ConfigureAwait(false);
        if (rsp.RetType != 0) // RetType_Succeed=0
        {
            throw new MoomooHistoryKLineException(
                $"moomoo RequestHistoryKL が失敗しました（retType={rsp.RetType}）: {rsp.RetMsg}");
        }

        var klines = new List<MoomooDailyKLine>();
        foreach (QotCommon.KLine kl in rsp.S2C.KlListList)
        {
            // 空白足（IsBlank）は「値の無い日を埋めた行」であり、価格として使うと架空のバーになる。
            if (kl.HasIsBlank && kl.IsBlank)
                continue;
            if (!TryParseKLineDate(kl.Time, out var date))
                continue;
            klines.Add(new MoomooDailyKLine(
                date,
                (decimal)kl.OpenPrice,
                (decimal)kl.HighPrice,
                (decimal)kl.LowPrice,
                (decimal)kl.ClosePrice,
                kl.Volume));
        }

        var nextKey = rsp.S2C.HasNextReqKey ? rsp.S2C.NextReqKey.ToByteArray() : null;
        return new MoomooHistoryKLinePage(klines, nextKey is { Length: > 0 } ? nextKey : null);
    }

    // ADR-0023 決定5: 復権方式の写像。**前復権（RehabType_Forward）から変えないこと**——
    // 無指定にすると株式分割を跨いで価格が不連続になり、バックテストの結果が壊れる。
    internal static int MapRehabType(MoomooKLineAdjustment adjustment) => adjustment switch
    {
        MoomooKLineAdjustment.ForwardAdjusted => (int)QotCommon.RehabType.RehabType_Forward,
        MoomooKLineAdjustment.BackwardAdjusted => (int)QotCommon.RehabType.RehabType_Backward,
        _ => (int)QotCommon.RehabType.RehabType_None,
    };

    // 日足の Time は "yyyy-MM-dd"（環境によっては時刻付き）。解釈できない行は採らない（架空の日付を作らない）。
    internal static bool TryParseKLineDate(string? time, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(time))
            return false;
        var head = time.Trim().Split(' ')[0];
        return DateOnly.TryParseExact(head, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    // 期間指定の書式（"yyyy-MM-dd"）。
    internal static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ---- 接続 ----

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_connected)
            return;

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connected)
                return;

            _connectTcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            _logger.LogInformation(
                "OpenD（相場・履歴 K 線）へ接続します {Host}:{Port} encrypt={Encrypt}",
                _options.OpenDHost, _options.OpenDPort, _encrypt);
            if (!_qot.InitConnect(_options.OpenDHost, _options.OpenDPort, _encrypt))
            {
                throw new InvalidOperationException(
                    $"OpenD への InitConnect が失敗しました（{_options.OpenDHost}:{_options.OpenDPort}）。");
            }
            await _connectTcs.Task.WaitAsync(_replyTimeout, cancellationToken).ConfigureAwait(false);
            _connected = true;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    // ---- 応答相関 ----

    private Task<QotRequestHistoryKL.Response> SendAsync(Func<uint> send, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<QotRequestHistoryKL.Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        // send() 直後にコールバックが返るレースを防ぐため、serial 採番と登録を _sendGate 内で原子的に行う。
        lock (_sendGate)
        {
            _pending[send()] = tcs;
        }
        return tcs.Task.WaitAsync(_replyTimeout, cancellationToken);
    }

    private void Complete(uint serial, QotRequestHistoryKL.Response rsp)
    {
        TaskCompletionSource<QotRequestHistoryKL.Response>? tcs;
        lock (_sendGate)
        {
            _pending.Remove(serial, out tcs);
        }
        tcs?.TrySetResult(rsp);
    }

    // ---- MMSPI_Conn ----

    public void OnInitConnect(MMAPI_Conn client, long errCode, string desc)
    {
        var tcs = _connectTcs;
        if (errCode == 0)
        {
            _logger.LogInformation("OpenD（相場）接続確立 connID={ConnId}", client.GetConnectID());
            tcs?.TrySetResult(errCode);
        }
        else
        {
            _logger.LogError("OpenD（相場）接続失敗 errCode={ErrCode} desc={Desc}", errCode, desc);
            tcs?.TrySetException(new InvalidOperationException($"OpenD 接続失敗 errCode={errCode}: {desc}"));
        }
    }

    public void OnDisconnect(MMAPI_Conn client, long errCode)
    {
        _connected = false;
        _logger.LogWarning("OpenD（相場）切断 errCode={ErrCode}", errCode);
    }

    // ---- MMSPI_Qot（使用するコールバック）----

    // 応答を捨てると RequestUsDailyKLinesAsync が応答待ちのままタイムアウトする。
    public void OnReply_RequestHistoryKL(MMAPI_Conn client, uint nSerialNo, QotRequestHistoryKL.Response rsp) =>
        Complete(nSerialNo, rsp);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _qot.Close();
            _qot.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenD（相場）クライアントの解放中に例外");
        }
        _connectGate.Dispose();
    }

    // ---- MMSPI_Qot（未使用・no-op）----
    // SDK のインターフェースが全 133 コールバックの実装を要求するため並べる（選択ではない）。

    public void OnReply_GetArkActiveTransaction(MMAPI_Conn client, uint nSerialNo, QotGetArkActiveTransaction.Response rsp) { }
    public void OnReply_GetArkFundHolding(MMAPI_Conn client, uint nSerialNo, QotGetArkFundHolding.Response rsp) { }
    public void OnReply_GetArkStockDynamic(MMAPI_Conn client, uint nSerialNo, QotGetArkStockDynamic.Response rsp) { }
    public void OnReply_GetBasicQot(MMAPI_Conn client, uint nSerialNo, QotGetBasicQot.Response rsp) { }
    public void OnReply_GetBroker(MMAPI_Conn client, uint nSerialNo, QotGetBroker.Response rsp) { }
    public void OnReply_GetCapitalDistribution(MMAPI_Conn client, uint nSerialNo, QotGetCapitalDistribution.Response rsp) { }
    public void OnReply_GetCapitalFlow(MMAPI_Conn client, uint nSerialNo, QotGetCapitalFlow.Response rsp) { }
    public void OnReply_GetCodeChange(MMAPI_Conn client, uint nSerialNo, QotGetCodeChange.Response rsp) { }
    public void OnReply_GetCompanyExecutiveBackground(MMAPI_Conn client, uint nSerialNo, QotGetCompanyExecutiveBackground.Response rsp) { }
    public void OnReply_GetCompanyExecutives(MMAPI_Conn client, uint nSerialNo, QotGetCompanyExecutives.Response rsp) { }
    public void OnReply_GetCompanyOperationalEfficiency(MMAPI_Conn client, uint nSerialNo, QotGetCompanyOperationalEfficiency.Response rsp) { }
    public void OnReply_GetCompanyProfile(MMAPI_Conn client, uint nSerialNo, QotGetCompanyProfile.Response rsp) { }
    public void OnReply_GetCorporateActionsBuybacks(MMAPI_Conn client, uint nSerialNo, QotGetCorporateActionsBuybacks.Response rsp) { }
    public void OnReply_GetCorporateActionsDividends(MMAPI_Conn client, uint nSerialNo, QotGetCorporateActionsDividends.Response rsp) { }
    public void OnReply_GetCorporateActionsStockSplits(MMAPI_Conn client, uint nSerialNo, QotGetCorporateActionsStockSplits.Response rsp) { }
    public void OnReply_GetDailyShortVolume(MMAPI_Conn client, uint nSerialNo, QotGetDailyShortVolume.Response rsp) { }
    public void OnReply_GetDerivativeUnusual(MMAPI_Conn client, uint nSerialNo, SkillWrapAPI.DerivativeUnusualRsp rsp) { }
    public void OnReply_GetDividendCalendar(MMAPI_Conn client, uint nSerialNo, QotGetDividendCalendar.Response rsp) { }
    public void OnReply_GetDividendRank(MMAPI_Conn client, uint nSerialNo, QotGetDividendRank.Response rsp) { }
    public void OnReply_GetEarningsBeatRank(MMAPI_Conn client, uint nSerialNo, QotGetEarningsBeatRank.Response rsp) { }
    public void OnReply_GetEarningsCalendar(MMAPI_Conn client, uint nSerialNo, QotGetEarningsCalendar.Response rsp) { }
    public void OnReply_GetEconomicCalendar(MMAPI_Conn client, uint nSerialNo, QotGetEconomicCalendar.Response rsp) { }
    public void OnReply_GetFedWatchDotPlot(MMAPI_Conn client, uint nSerialNo, QotGetFedWatchDotPlot.Response rsp) { }
    public void OnReply_GetFedWatchTargetRate(MMAPI_Conn client, uint nSerialNo, QotGetFedWatchTargetRate.Response rsp) { }
    public void OnReply_GetFinancialsEarningsPriceHistory(MMAPI_Conn client, uint nSerialNo, QotGetFinancialsEarningsPriceHistory.Response rsp) { }
    public void OnReply_GetFinancialsEarningsPriceMove(MMAPI_Conn client, uint nSerialNo, QotGetFinancialsEarningsPriceMove.Response rsp) { }
    public void OnReply_GetFinancialsRevenueBreakdown(MMAPI_Conn client, uint nSerialNo, QotGetFinancialsRevenueBreakdown.Response rsp) { }
    public void OnReply_GetFinancialsStatements(MMAPI_Conn client, uint nSerialNo, QotGetFinancialsStatements.Response rsp) { }
    public void OnReply_GetFinancialUnusual(MMAPI_Conn client, uint nSerialNo, SkillWrapAPI.FinancialUnusualRsp rsp) { }
    public void OnReply_GetFutureInfo(MMAPI_Conn client, uint nSerialNo, QotGetFutureInfo.Response rsp) { }
    public void OnReply_GetGlobalState(MMAPI_Conn client, uint nSerialNo, GetGlobalState.Response rsp) { }
    public void OnReply_GetHeatMapData(MMAPI_Conn client, uint nSerialNo, QotGetHeatMapData.Response rsp) { }
    public void OnReply_GetHighDividendSOERank(MMAPI_Conn client, uint nSerialNo, QotGetHighDividendSOERank.Response rsp) { }
    public void OnReply_GetHoldingChangeList(MMAPI_Conn client, uint nSerialNo, QotGetHoldingChangeList.Response rsp) { }
    public void OnReply_GetHotList(MMAPI_Conn client, uint nSerialNo, QotGetHotList.Response rsp) { }
    public void OnReply_GetIndicatorList(MMAPI_Conn client, uint nSerialNo, QotGetIndicatorList.Response rsp) { }
    public void OnReply_GetIndustrialChainByPlate(MMAPI_Conn client, uint nSerialNo, QotGetIndustrialChainByPlate.Response rsp) { }
    public void OnReply_GetIndustrialChainDetail(MMAPI_Conn client, uint nSerialNo, QotGetIndustrialChainDetail.Response rsp) { }
    public void OnReply_GetIndustrialChainList(MMAPI_Conn client, uint nSerialNo, QotGetIndustrialChainList.Response rsp) { }
    public void OnReply_GetIndustrialPlateInfo(MMAPI_Conn client, uint nSerialNo, QotGetIndustrialPlateInfo.Response rsp) { }
    public void OnReply_GetIndustrialPlateStock(MMAPI_Conn client, uint nSerialNo, QotGetIndustrialPlateStock.Response rsp) { }
    public void OnReply_GetInsiderHolderList(MMAPI_Conn client, uint nSerialNo, QotGetInsiderHolderList.Response rsp) { }
    public void OnReply_GetInsiderTradeList(MMAPI_Conn client, uint nSerialNo, QotGetInsiderTradeList.Response rsp) { }
    public void OnReply_GetInstitutionDistribution(MMAPI_Conn client, uint nSerialNo, QotGetInstitutionDistribution.Response rsp) { }
    public void OnReply_GetInstitutionHoldingChange(MMAPI_Conn client, uint nSerialNo, QotGetInstitutionHoldingChange.Response rsp) { }
    public void OnReply_GetInstitutionHoldingList(MMAPI_Conn client, uint nSerialNo, QotGetInstitutionHoldingList.Response rsp) { }
    public void OnReply_GetInstitutionList(MMAPI_Conn client, uint nSerialNo, QotGetInstitutionList.Response rsp) { }
    public void OnReply_GetInstitutionProfile(MMAPI_Conn client, uint nSerialNo, QotGetInstitutionProfile.Response rsp) { }
    public void OnReply_GetIpoList(MMAPI_Conn client, uint nSerialNo, QotGetIpoList.Response rsp) { }
    public void OnReply_GetKL(MMAPI_Conn client, uint nSerialNo, QotGetKL.Response rsp) { }
    public void OnReply_GetMacroIndicatorHistory(MMAPI_Conn client, uint nSerialNo, QotGetMacroIndicatorHistory.Response rsp) { }
    public void OnReply_GetMacroIndicatorList(MMAPI_Conn client, uint nSerialNo, QotGetMacroIndicatorList.Response rsp) { }
    public void OnReply_GetMarketState(MMAPI_Conn client, uint nSerialNo, QotGetMarketState.Response rsp) { }
    public void OnReply_GetOptionChain(MMAPI_Conn client, uint nSerialNo, QotGetOptionChain.Response rsp) { }
    public void OnReply_GetOptionEarnings(MMAPI_Conn client, uint nSerialNo, QotGetOptionEarningsScreener.Response rsp) { }
    public void OnReply_GetOptionEvent(MMAPI_Conn client, uint nSerialNo, QotGetOptionEvent.Response rsp) { }
    public void OnReply_GetOptionEventAlert(MMAPI_Conn client, uint nSerialNo, QotGetOptionEventAlert.Response rsp) { }
    public void OnReply_GetOptionExerciseProbability(MMAPI_Conn client, uint nSerialNo, QotGetOptionExerciseProbability.Response rsp) { }
    public void OnReply_GetOptionExpirationDate(MMAPI_Conn client, uint nSerialNo, QotGetOptionExpirationDate.Response rsp) { }
    public void OnReply_GetOptionMarketStatistic(MMAPI_Conn client, uint nSerialNo, QotGetOptionMarketStatistic.Response rsp) { }
    public void OnReply_GetOptionQuote(MMAPI_Conn client, uint nSerialNo, QotGetOptionQuote.Response rsp) { }
    public void OnReply_GetOptionRank(MMAPI_Conn client, uint nSerialNo, QotGetOptionRank.Response rsp) { }
    public void OnReply_GetOptionScreen(MMAPI_Conn client, uint nSerialNo, QotOptionScreen.Response rsp) { }
    public void OnReply_GetOptionSellerScreener(MMAPI_Conn client, uint nSerialNo, QotGetOptionSellerScreener.Response rsp) { }
    public void OnReply_GetOptionStrategy(MMAPI_Conn client, uint nSerialNo, QotGetOptionStrategy.Response rsp) { }
    public void OnReply_GetOptionStrategyAnalysis(MMAPI_Conn client, uint nSerialNo, QotGetOptionStrategyAnalysis.Response rsp) { }
    public void OnReply_GetOptionStrategySpread(MMAPI_Conn client, uint nSerialNo, QotGetOptionStrategySpread.Response rsp) { }
    public void OnReply_GetOptionUnderlyingHisStatistic(MMAPI_Conn client, uint nSerialNo, QotGetOptionUnderlyingHisStatistic.Response rsp) { }
    public void OnReply_GetOptionUnderlyingHisVolatility(MMAPI_Conn client, uint nSerialNo, QotGetOptionUnderlyingHisVolatility.Response rsp) { }
    public void OnReply_GetOptionUnderlyingOverview(MMAPI_Conn client, uint nSerialNo, QotGetOptionUnderlyingOverview.Response rsp) { }
    public void OnReply_GetOptionUnderlyingRank(MMAPI_Conn client, uint nSerialNo, QotGetOptionUnderlyingRank.Response rsp) { }
    public void OnReply_GetOptionVolatility(MMAPI_Conn client, uint nSerialNo, QotGetOptionVolatility.Response rsp) { }
    public void OnReply_GetOptionZeroDteContract(MMAPI_Conn client, uint nSerialNo, QotGetOptionZeroDteContract.Response rsp) { }
    public void OnReply_GetOptionZeroDteScreener(MMAPI_Conn client, uint nSerialNo, QotGetOptionZeroDteScreener.Response rsp) { }
    public void OnReply_GetOrderBook(MMAPI_Conn client, uint nSerialNo, QotGetOrderBook.Response rsp) { }
    public void OnReply_GetOwnerPlate(MMAPI_Conn client, uint nSerialNo, QotGetOwnerPlate.Response rsp) { }
    public void OnReply_GetPeriodChangeRank(MMAPI_Conn client, uint nSerialNo, QotGetPeriodChangeRank.Response rsp) { }
    public void OnReply_GetPlateSecurity(MMAPI_Conn client, uint nSerialNo, QotGetPlateSecurity.Response rsp) { }
    public void OnReply_GetPlateSet(MMAPI_Conn client, uint nSerialNo, QotGetPlateSet.Response rsp) { }
    public void OnReply_GetPriceReminder(MMAPI_Conn client, uint nSerialNo, QotGetPriceReminder.Response rsp) { }
    public void OnReply_GetRatingChange(MMAPI_Conn client, uint nSerialNo, QotGetRatingChange.Response rsp) { }
    public void OnReply_GetReference(MMAPI_Conn client, uint nSerialNo, QotGetReference.Response rsp) { }
    public void OnReply_GetResearchAnalystConsensus(MMAPI_Conn client, uint nSerialNo, QotGetResearchAnalystConsensus.Response rsp) { }
    public void OnReply_GetResearchMorningstarReport(MMAPI_Conn client, uint nSerialNo, QotGetResearchMorningstarReport.Response rsp) { }
    public void OnReply_GetResearchRatingSummary(MMAPI_Conn client, uint nSerialNo, QotGetResearchRatingSummary.Response rsp) { }
    public void OnReply_GetRiseFallDistribution(MMAPI_Conn client, uint nSerialNo, QotGetRiseFallDistribution.Response rsp) { }
    public void OnReply_GetRT(MMAPI_Conn client, uint nSerialNo, QotGetRT.Response rsp) { }
    public void OnReply_GetSearchNews(MMAPI_Conn client, uint nSerialNo, QotGetSearchNews.Response rsp) { }
    public void OnReply_GetSearchQuote(MMAPI_Conn client, uint nSerialNo, QotGetSearchQuote.Response rsp) { }
    public void OnReply_GetSecuritySnapshot(MMAPI_Conn client, uint nSerialNo, QotGetSecuritySnapshot.Response rsp) { }
    public void OnReply_GetShareholdersHolderDetail(MMAPI_Conn client, uint nSerialNo, QotGetShareholdersHolderDetail.Response rsp) { }
    public void OnReply_GetShareholdersHoldingChanges(MMAPI_Conn client, uint nSerialNo, QotGetShareholdersHoldingChanges.Response rsp) { }
    public void OnReply_GetShareholdersInstitutional(MMAPI_Conn client, uint nSerialNo, QotGetShareholdersInstitutional.Response rsp) { }
    public void OnReply_GetShareholdersOverview(MMAPI_Conn client, uint nSerialNo, QotGetShareholdersOverview.Response rsp) { }
    public void OnReply_GetShortInterest(MMAPI_Conn client, uint nSerialNo, QotGetShortInterest.Response rsp) { }
    public void OnReply_GetShortSellingRank(MMAPI_Conn client, uint nSerialNo, QotGetShortSellingRank.Response rsp) { }
    public void OnReply_GetStaticInfo(MMAPI_Conn client, uint nSerialNo, QotGetStaticInfo.Response rsp) { }
    public void OnReply_GetStockScreen(MMAPI_Conn client, uint nSerialNo, QotStockScreen.Response rsp) { }
    public void OnReply_GetSubInfo(MMAPI_Conn client, uint nSerialNo, QotGetSubInfo.Response rsp) { }
    public void OnReply_GetTechnicalUnusual(MMAPI_Conn client, uint nSerialNo, SkillWrapAPI.TechnicalUnusualRsp rsp) { }
    public void OnReply_GetTicker(MMAPI_Conn client, uint nSerialNo, QotGetTicker.Response rsp) { }
    public void OnReply_GetTopMoversRank(MMAPI_Conn client, uint nSerialNo, QotGetTopMoversRank.Response rsp) { }
    public void OnReply_GetTopTenBuySellBrokers(MMAPI_Conn client, uint nSerialNo, QotGetTopTenBuySellBrokers.Response rsp) { }
    public void OnReply_GetUSAfterHoursRank(MMAPI_Conn client, uint nSerialNo, QotGetUSAfterHoursRank.Response rsp) { }
    public void OnReply_GetUserSecurity(MMAPI_Conn client, uint nSerialNo, QotGetUserSecurity.Response rsp) { }
    public void OnReply_GetUserSecurityGroup(MMAPI_Conn client, uint nSerialNo, QotGetUserSecurityGroup.Response rsp) { }
    public void OnReply_GetUSOvernightRank(MMAPI_Conn client, uint nSerialNo, QotGetUSOvernightRank.Response rsp) { }
    public void OnReply_GetUSPreMarketRank(MMAPI_Conn client, uint nSerialNo, QotGetUSPreMarketRank.Response rsp) { }
    public void OnReply_GetValuationDetail(MMAPI_Conn client, uint nSerialNo, QotGetValuationDetail.Response rsp) { }
    public void OnReply_GetValuationPlateStockList(MMAPI_Conn client, uint nSerialNo, QotGetValuationPlateStockList.Response rsp) { }
    public void OnReply_GetWarrant(MMAPI_Conn client, uint nSerialNo, QotGetWarrant.Response rsp) { }
    public void OnReply_GetWarrantScreen(MMAPI_Conn client, uint nSerialNo, QotWarrantScreen.Response rsp) { }
    public void OnReply_ModifyUserSecurity(MMAPI_Conn client, uint nSerialNo, QotModifyUserSecurity.Response rsp) { }
    public void OnReply_Notify(MMAPI_Conn client, uint nSerialNo, Notify.Response rsp) { }
    public void OnReply_PushIndicatorCalc(MMAPI_Conn client, uint nSerialNo, QotPushIndicatorCalc.Response rsp) { }
    public void OnReply_RegQotPush(MMAPI_Conn client, uint nSerialNo, QotRegQotPush.Response rsp) { }
    public void OnReply_RequestHistoryKLQuota(MMAPI_Conn client, uint nSerialNo, QotRequestHistoryKLQuota.Response rsp) { }
    public void OnReply_RequestIndicatorCalc(MMAPI_Conn client, uint nSerialNo, QotRequestIndicatorCalc.Response rsp) { }
    public void OnReply_RequestRehab(MMAPI_Conn client, uint nSerialNo, QotRequestRehab.Response rsp) { }
    public void OnReply_RequestTradeDate(MMAPI_Conn client, uint nSerialNo, QotRequestTradeDate.Response rsp) { }
    public void OnReply_SetOptionEventAlert(MMAPI_Conn client, uint nSerialNo, QotSetOptionEventAlert.Response rsp) { }
    public void OnReply_SetPriceReminder(MMAPI_Conn client, uint nSerialNo, QotSetPriceReminder.Response rsp) { }
    public void OnReply_StockFilter(MMAPI_Conn client, uint nSerialNo, QotStockFilter.Response rsp) { }
    public void OnReply_Sub(MMAPI_Conn client, uint nSerialNo, QotSub.Response rsp) { }
    public void OnReply_UpdateBasicQot(MMAPI_Conn client, uint nSerialNo, QotUpdateBasicQot.Response rsp) { }
    public void OnReply_UpdateBroker(MMAPI_Conn client, uint nSerialNo, QotUpdateBroker.Response rsp) { }
    public void OnReply_UpdateKL(MMAPI_Conn client, uint nSerialNo, QotUpdateKL.Response rsp) { }
    public void OnReply_UpdateOptionEvent(MMAPI_Conn client, uint nSerialNo, QotUpdateOptionEvent.Response rsp) { }
    public void OnReply_UpdateOrderBook(MMAPI_Conn client, uint nSerialNo, QotUpdateOrderBook.Response rsp) { }
    public void OnReply_UpdatePriceReminder(MMAPI_Conn client, uint nSerialNo, QotUpdatePriceReminder.Response rsp) { }
    public void OnReply_UpdateRT(MMAPI_Conn client, uint nSerialNo, QotUpdateRT.Response rsp) { }
    public void OnReply_UpdateTicker(MMAPI_Conn client, uint nSerialNo, QotUpdateTicker.Response rsp) { }
}
