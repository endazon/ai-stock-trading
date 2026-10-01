using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;

// FR-01, ADR-0031（計画）決定2〜4, ADR-0043（計画）決定 1・3, #1132, IADR-0477: 巡回するプロセス（市場監視・リスク管理）の
// Finnhub 日次要求見積りを、**巡回ごとに、その巡回で問い合わせる銘柄の実数から**記録する。
//
// 是正前（IADR-0294）は運用者の申告銘柄数（`MarketData:Finnhub:EstimatedSymbolCount`）を起動時に 1 回だけ数えていた。
// 申告は 1 に固定され、実測と桁で外れた（市場監視 390 に対し実測 ≈ 3,510。#1132）。銘柄の実数（保有・監視銘柄）は
// 起動時には決まらないが、巡回の中では毎回手元にある——そこで巡回のたびに数え直す。
//
// - 記録するのは Provider が finnhub で鍵があるときだけ（それ以外は Finnhub へ送らない＝見積る量が無い）。
// - メトリクスは毎回記録する（ゲージは最後の値）。ログは値が変わったときだけ出す（巡回ごとに積もらせない）。
// - 日次上限は既定で未設定（未実測。ADR-0043 決定 1）＝比べない。設定したときだけ超過を警告する。**送出は止めない。**
public sealed class FinnhubDailyVolumeRecorder
{
    private readonly bool _active;
    private readonly int? _dailyLimit;
    private readonly BusinessMetrics _metrics;
    private readonly ILogger _logger;
    private readonly Func<Market, int> _sessionMinutes;
    private long _lastLogged = -1;

    /// <param name="options">MarketData 構成（Provider と鍵だけを見る）。</param>
    /// <param name="dailyVolumeGuard">日次上限（既定は未設定）。</param>
    /// <param name="metrics">記録先の業務メトリクス。</param>
    /// <param name="logger">ログの出力先。</param>
    /// <param name="sessionMinutes">市場ごとの場中の分（共有カーネルの <c>MarketSessions.RegularSessionMinutes</c>）。</param>
    public FinnhubDailyVolumeRecorder(
        MarketDataOptions options,
        FinnhubDailyVolumeGuardOptions dailyVolumeGuard,
        BusinessMetrics metrics,
        ILogger logger,
        Func<Market, int> sessionMinutes)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dailyVolumeGuard);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(sessionMinutes);

        _active = MarketDataSourceFactory.SendsToFinnhub(options);
        _dailyLimit = dailyVolumeGuard.ProvisionalDailyLimit;
        _metrics = metrics;
        _logger = logger;
        _sessionMinutes = sessionMinutes;
    }

    /// <summary>
    /// 1 巡回で問い合わせる銘柄の市場から日次要求数を見積もって記録し、その値を返す。Finnhub へ送らない構成では
    /// 記録せず null を返す。
    /// </summary>
    /// <param name="symbolMarkets">
    /// この巡回の対象の銘柄の市場。<b>閉場中で照会を飛ばした銘柄も含める</b>（見積りは開場中の量であり、
    /// 照会した数だけで数えると、ある市場だけ閉じた巡回で値が落ちる）。
    /// </param>
    /// <param name="pollIntervalSeconds">当プロセスの巡回間隔（秒）。</param>
    public long? Record(IEnumerable<Market> symbolMarkets, int pollIntervalSeconds)
    {
        ArgumentNullException.ThrowIfNull(symbolMarkets);
        if (!_active)
            return null;

        var markets = symbolMarkets.ToArray();
        var estimated = FinnhubDailyVolumeEstimator.EstimateForSymbols(markets, pollIntervalSeconds, _sessionMinutes);
        var result = FinnhubDailyVolumeEstimator.Evaluate(estimated, _dailyLimit);
        _metrics.RecordFinnhubDailyVolumeEstimate(result.EstimatedDailyRequests, result.ExceedRatio * 100);

        if (Interlocked.Exchange(ref _lastLogged, estimated) == estimated)
            return estimated;

        var symbols = markets.Sum(FinnhubDailyVolumeEstimator.RequestsPerSymbol);
        var interval = Math.Max(1, pollIntervalSeconds);
        if (result.Verdict == FinnhubDailyVolumeEstimator.Verdict.Exceeds)
        {
            _logger.LogWarning(
                "Finnhub の日次要求見積り {Estimated} 回/日（1 巡回で問い合わせる米国の銘柄 {Symbols} 件 × 場中の巡回。巡回 {Interval} 秒）が"
                + "設定された日次上限 {Limit} 回/日を超えています（ADR-0031 決定3）。送出は継続します（統制は警告のみ）。",
                estimated, symbols, interval, _dailyLimit);
        }
        else if (result.Verdict == FinnhubDailyVolumeEstimator.Verdict.NotCompared)
        {
            _logger.LogInformation(
                "Finnhub の日次要求見積り {Estimated} 回/日（1 巡回で問い合わせる米国の銘柄 {Symbols} 件 × 場中の巡回。巡回 {Interval} 秒）。"
                + "保有・監視銘柄の実数から数えた値です。日次上限は未実測のため比べません（ADR-0043 決定1）。日次は 429 で見張ります。",
                estimated, symbols, interval);
        }

        return estimated;
    }
}
