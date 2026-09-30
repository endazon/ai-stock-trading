using System.Globalization;
using Moomoo.OpenApi;
using Moomoo.OpenApi.Pb;
using OrderExecutionService.Features.OrderExecution.ProbeKLineQuota;

namespace OrderExecutionService.Infrastructure.ExternalServices;

// FR-02, FR-15, ADR-0048 決定 3, ADR-0023 決定 5, #1117, IADR-0464: K 線の検証口（--probe-kline-quota）専用の
// moomoo 相場（Qot）クライアント。**履歴 K 線の取得枠の照会（QotRequestHistoryKLQuota）と、米国株の日足 K 線の
// 1 リクエスト（QotRequestHistoryKL）だけ**を撃つ。
//
// 要求の組み立て（Security・KLType_Day・RehabType・期間の書式・MaxAckKLNum・NeedKLFieldsFlag）は BacktestService の
// MMApiMoomooHistoryKLineClient（IADR-0157）に合わせる。サービス間の直接参照はできないため、同じ呼び方をこちらに置く（IADR-0464 決定 1）。
//
// 🔴 **MMSPI_Trd を実装しない・発注の接続（IMoomooTradeConnection）を参照しない。** 本クラスから発注・訂正・取消・口座の
// 照会へ届く経路は無く、口座番号も扱わない（試験で型を固定）。
// 🔴 **1 呼び出しにつき OpenD へ 1 回だけ撃つ。** 再試行・ページングのループを持たない。接続も作り直さない（1 回実行のため）。
// 🔴 **ログを持たない。** 検証口の出力は検証口（KLineQuotaProbeCommand）が伏せてから書く。本クラスは例外文に接続先を載せ得るが、
// 鍵の内容は載せない。
//
// MMSPI_Qot は 133 のコールバックの実装を要するインターフェースである。使うのは OnReply_RequestHistoryKL と
// OnReply_RequestHistoryKLQuota の 2 つだけであり、残りは no-op として並べる（SDK の要求であって選択ではない）。
public sealed class MMApiMoomooKLineProbeClient : MMSPI_Qot, MMSPI_Conn, IKLineQuotaQuery, IDisposable
{
    // 米国株（QotMarket_US_Security）。検証口は米国株だけを扱う（ADR-0023 決定 5 の対象）。
    public const int UsSecurityMarket = (int)QotCommon.QotMarket.QotMarket_US_Security;

    // 日足（KLType_Day）。
    public const int DayKLType = (int)QotCommon.KLType.KLType_Day;

    // 1 リクエストで求める最大の本数（OpenD の上限 1,000。ADR-0023 決定 5）。検証口は 1 ページだけ取る。
    public const int MaxAckKLNum = 1000;

    // 始高安終・出来高・売買代金を要求する。要求しない項目は応答に載らない。
    public const long ProbeKLFields =
        (long)QotCommon.KLFields.KLFields_High | (long)QotCommon.KLFields.KLFields_Open |
        (long)QotCommon.KLFields.KLFields_Low | (long)QotCommon.KLFields.KLFields_Close |
        (long)QotCommon.KLFields.KLFields_Volume | (long)QotCommon.KLFields.KLFields_Turnover;

    private readonly MoomooBrokerOptions _options;
    private readonly TimeSpan _replyTimeout;
    private readonly IMoomooQotProbeConnection _connection;
    private readonly Dictionary<uint, TaskCompletionSource<object>> _pending = [];
    private readonly object _sendGate = new(); // serial 採番＋登録とコールバック完了の相互排他（レース防止）。
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly bool _encrypt;
    private TaskCompletionSource<long>? _connectTcs;
    private volatile bool _connected;
    private bool _disposed;

    // connectionFactory は接続オブジェクトの生成点。既定は本番の SDK 実装で、試験は偽の OpenD を差す。
    // replyTimeout は応答待ち。null なら構成（Broker:Moomoo:OpenD:ReplyTimeoutSeconds・既定 15 秒）。
    public MMApiMoomooKLineProbeClient(
        MoomooBrokerOptions options,
        IMoomooQotProbeConnectionFactory? connectionFactory = null,
        TimeSpan? replyTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        // IADR-0060: 鍵のマウント漏れは接続の前に落とす（発注の接続と同じ preflight）。
        MoomooPreflight.Validate(options, File.Exists);
        _options = options;
        _replyTimeout = replyTimeout ?? options.ReplyTimeout;
        MoomooApi.EnsureInitialized();
        _connection = (connectionFactory ?? new MMApiQotProbeConnectionFactory()).Create();
        _connection.SetClientInfo("ai-stock-trading", 1);
        _connection.SetConnCallback(this);
        _connection.SetQotCallback(this);
        // cross-network の OpenD へは発注の接続と同じ RSA 鍵で暗号化して繋ぐ（2026-09-02 の読み取り専用 probe が
        // 同じ鍵の暗号化接続で Qot の要求に成功している）。鍵の内容は保持せずに渡すだけにする。
        if (!string.IsNullOrWhiteSpace(options.RsaPrivateKeyPath))
        {
            _connection.SetRsaPrivateKey(File.ReadAllText(options.RsaPrivateKeyPath));
            _encrypt = true;
        }
    }

    // ---- IKLineQuotaQuery ----

    public async Task<KLineQuotaReading> QueryQuotaAsync(bool includeDetail, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var c2s = QotRequestHistoryKLQuota.C2S.CreateBuilder().SetBGetDetail(includeDetail).Build();
        var req = QotRequestHistoryKLQuota.Request.CreateBuilder().SetC2S(c2s).Build();
        var rsp = (QotRequestHistoryKLQuota.Response)await SendAsync(
            () => _connection.RequestHistoryKLQuota(req), cancellationToken).ConfigureAwait(false);
        return MapQuota(rsp);
    }

    public async Task<DailyKLineReading> RequestDailyKLinesAsync(
        DailyKLineProbeRequest request, CancellationToken cancellationToken = default)
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
            .SetRehabType(MapRehabType(request.Rehab))
            .SetBeginTime(FormatDate(request.From))
            .SetEndTime(FormatDate(request.To))
            .SetMaxAckKLNum(MaxAckKLNum)
            .SetNeedKLFieldsFlag(ProbeKLFields)
            .Build();
        var req = QotRequestHistoryKL.Request.CreateBuilder().SetC2S(c2s).Build();
        var rsp = (QotRequestHistoryKL.Response)await SendAsync(
            () => _connection.RequestHistoryKL(req), cancellationToken).ConfigureAwait(false);
        return MapKLines(rsp);
    }

    public static int MapRehabType(KLineRehab rehab) => rehab switch
    {
        KLineRehab.None => (int)QotCommon.RehabType.RehabType_None,
        KLineRehab.Forward => (int)QotCommon.RehabType.RehabType_Forward,
        KLineRehab.Backward => (int)QotCommon.RehabType.RehabType_Backward,
        _ => throw new ArgumentOutOfRangeException(nameof(rehab), rehab, "未知の復権区分"),
    };

    // 期間の書式（"yyyy-MM-dd"。BacktestService の MMApiMoomooHistoryKLineClient.FormatDate と同じ）。
    public static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static KLineQuotaReading MapQuota(QotRequestHistoryKLQuota.Response rsp)
    {
        ArgumentNullException.ThrowIfNull(rsp);
        var details = new List<KLineQuotaDetail>();
        int? used = null;
        int? remain = null;
        if (rsp.HasS2C)
        {
            var s2c = rsp.S2C;
            used = s2c.HasUsedQuota ? s2c.UsedQuota : null;
            remain = s2c.HasRemainQuota ? s2c.RemainQuota : null;
            foreach (QotRequestHistoryKLQuota.DetailItem item in s2c.DetailListList)
            {
                details.Add(new KLineQuotaDetail(
                    item.HasSecurity ? FormatSecurity(item.Security) : null,
                    item.HasName ? item.Name : null,
                    item.HasRequestTime ? item.RequestTime : null,
                    item.HasRequestTimeStamp ? item.RequestTimeStamp : null));
            }
        }
        return new KLineQuotaReading(rsp.RetType, rsp.HasRetMsg ? rsp.RetMsg : string.Empty, used, remain, details);
    }

    public static DailyKLineReading MapKLines(QotRequestHistoryKL.Response rsp)
    {
        ArgumentNullException.ThrowIfNull(rsp);
        var klines = new List<ProbeKLine>();
        var hasMore = false;
        if (rsp.HasS2C)
        {
            foreach (QotCommon.KLine kl in rsp.S2C.KlListList)
            {
                klines.Add(new ProbeKLine(
                    kl.HasTime ? kl.Time : null,
                    kl.HasIsBlank && kl.IsBlank,
                    kl.HasOpenPrice ? kl.OpenPrice : null,
                    kl.HasHighPrice ? kl.HighPrice : null,
                    kl.HasLowPrice ? kl.LowPrice : null,
                    kl.HasClosePrice ? kl.ClosePrice : null,
                    kl.HasVolume ? kl.Volume : null,
                    kl.HasTurnover ? kl.Turnover : null));
            }
            hasMore = rsp.S2C.HasNextReqKey && rsp.S2C.NextReqKey.Length > 0;
        }
        return new DailyKLineReading(rsp.RetType, rsp.HasRetMsg ? rsp.RetMsg : string.Empty, klines, hasMore);
    }

    private static string FormatSecurity(QotCommon.Security security) =>
        (security.Market == UsSecurityMarket ? "US" : $"market{security.Market.ToString(CultureInfo.InvariantCulture)}")
        + "." + security.Code;

    // ---- 接続（1 回だけ。作り直さない）----

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
            if (!_connection.InitConnect(_options.OpenDHost, _options.OpenDPort, _encrypt))
            {
                throw new InvalidOperationException(
                    $"OpenD（相場）への InitConnect が失敗しました（{_options.OpenDHost}:{_options.OpenDPort}）。");
            }
            await _connectTcs.Task.WaitAsync(_replyTimeout, cancellationToken).ConfigureAwait(false);
            _connected = true;
        }
        finally
        {
            _connectTcs = null;
            _connectGate.Release();
        }
    }

    // ---- 応答相関 ----

    private Task<object> SendAsync(Func<uint> send, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        // send() 直後にコールバックが返るレースを防ぐため、serial 採番と登録を _sendGate 内で原子的に行う。
        lock (_sendGate)
        {
            _pending[send()] = tcs;
        }
        return tcs.Task.WaitAsync(_replyTimeout, cancellationToken);
    }

    private void Complete(uint serial, object rsp)
    {
        TaskCompletionSource<object>? tcs;
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
            tcs?.TrySetResult(errCode);
        else
            tcs?.TrySetException(new InvalidOperationException($"OpenD（相場）接続失敗 errCode={errCode}: {desc}"));
    }

    public void OnDisconnect(MMAPI_Conn client, long errCode) => _connected = false;

    // ---- MMSPI_Qot（使用するコールバック）----

    public void OnReply_RequestHistoryKL(MMAPI_Conn client, uint nSerialNo, QotRequestHistoryKL.Response rsp) =>
        Complete(nSerialNo, rsp);

    public void OnReply_RequestHistoryKLQuota(MMAPI_Conn client, uint nSerialNo, QotRequestHistoryKLQuota.Response rsp) =>
        Complete(nSerialNo, rsp);

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
        catch (Exception)
        {
            // 1 回実行の後始末。解放の失敗で検証の結果（出力済み）を覆さない。
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
