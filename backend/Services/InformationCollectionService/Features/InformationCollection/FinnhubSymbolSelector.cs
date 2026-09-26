using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using InformationCollectionService.Domain;
using Microsoft.Extensions.Logging;

namespace InformationCollectionService.Features.InformationCollection;

// FR-01, FR-02, FR-13, #1015, IADR-0435: Finnhub の対象銘柄を決める。
//
// 1. 市場監視の結線があれば、毎巡回の最初に監視銘柄を読み、**米国市場の銘柄**を監視銘柄の順（重複は最初の 1 件）で採る
//    （Finnhub Free が扱うのは米国株。市場監視の Finnhub 実装も米国以外を飛ばす）。
// 2. 🔴 **原則 A: 不明は空ではない。** 読めなければ直前に読めた集合を使い続け、それも無ければ構成の固定リストへ倒す。
//    どちらも警告し、出所をメトリクスで数える。読めて 0 件なら 0 件（構成へ倒さない。事実としての空である）。
// 3. 計画 ADR-0043 決定2 (b): 1 巡回の要求が巡回間隔に収まる数だけを**並びの先頭から**採り、後回しにした銘柄を警告と
//    メトリクスで見せる（FinnhubCycleFit）。自制レートそのものはトークンバケットが守る。
//
// 集合は巡回ごとに 1 回だけ差し替える（参照の差し替えのみ。読み手はロックを取らない）。直前の値は**読めたときだけ**更新する。
public sealed class FinnhubSymbolSelector : IFinnhubSymbolSet
{
    public const string OutcomeWatchlist = "watchlist";
    public const string OutcomeLastKnown = "last-known";
    public const string OutcomeConfiguredFallback = "configured-fallback";

    private readonly IWatchlistReader? _reader;
    private readonly IReadOnlyList<string> _configured;
    private readonly int _maxSymbols;
    private readonly BusinessMetrics _metrics;
    private readonly ILogger<FinnhubSymbolSelector> _logger;

    private IReadOnlyList<string> _current;
    private IReadOnlyList<string>? _lastKnown;

    /// <param name="reader">市場監視の読み口。<c>null</c>＝未結線（構成の固定リストを使い、照会しない）。</param>
    /// <param name="configured">構成の固定リスト（未結線時の対象・結線時のフォールバック）。</param>
    /// <param name="maxSymbolsPerCycle">1 巡回に収まる銘柄数（<see cref="FinnhubCycleFit.MaxSymbolsPerCycle"/>）。</param>
    public FinnhubSymbolSelector(
        IWatchlistReader? reader,
        IReadOnlyList<string> configured,
        int maxSymbolsPerCycle,
        BusinessMetrics metrics,
        ILogger<FinnhubSymbolSelector> logger)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentOutOfRangeException.ThrowIfNegative(maxSymbolsPerCycle);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _reader = reader;
        _configured = Distinct(configured);
        _maxSymbols = maxSymbolsPerCycle;
        _metrics = metrics;
        _logger = logger;

        // 最初の巡回の前（と未結線時のずっと）は構成の固定リスト。上限は同じく掛ける（ADR-0043 決定2 (b) は出所を問わない）。
        _current = Fit(_configured, "構成");
    }

    /// <summary>市場監視の監視銘柄に追随するか（結線されているか）。</summary>
    public bool FollowsWatchlist => _reader is not null;

    public IReadOnlyList<string> Current => Volatile.Read(ref _current);

    /// <summary>
    /// 監視銘柄を読み直して集合を差し替える（巡回の最初に 1 回呼ぶ）。未結線なら何もしない。
    /// 呼び出し側の中止以外の失敗は投げない（読めない＝不明として直前の値・構成へ倒す）。
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_reader is null)
            return;

        var read = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<string> basis;
        if (read is not null)
        {
            basis = UnitedStatesSymbols(read);
            _lastKnown = basis;
            _metrics.RecordFinnhubSymbolSetResolution(OutcomeWatchlist);
            _logger.LogInformation(
                "監視銘柄から Finnhub の対象を決めました: 米国の銘柄 {UsCount} 件（監視銘柄 {Total} 件）。",
                basis.Count, read.Count);
        }
        else if (_lastKnown is not null)
        {
            basis = _lastKnown;
            _metrics.RecordFinnhubSymbolSetResolution(OutcomeLastKnown);
            _logger.LogWarning(
                "市場監視の監視銘柄を読めなかったため、直前に読めた対象 {Count} 件（{Symbols}）で収集します"
                + "（不明を空と扱わない。監視銘柄の変更は読めるまで収集に反映されません）。",
                basis.Count, string.Join(",", basis));
        }
        else
        {
            basis = _configured;
            _metrics.RecordFinnhubSymbolSetResolution(OutcomeConfiguredFallback);
            _logger.LogWarning(
                "市場監視の監視銘柄を一度も読めていないため、構成の固定リスト（Collection:Source:Finnhub:Symbols）{Count} 件"
                + "（{Symbols}）で収集します（不明を空と扱わない）。",
                basis.Count, string.Join(",", basis));
        }

        Volatile.Write(ref _current, Fit(basis, read is not null ? "監視銘柄" : "代替"));
    }

    // ADR-0043 決定2 (b): 収まる数だけを先頭から採る。後回しの数は毎回記録する（平常 0。0 を記録しないと回復が見えない）。
    private IReadOnlyList<string> Fit(IReadOnlyList<string> basis, string origin)
    {
        var deferred = Math.Max(0, basis.Count - _maxSymbols);
        _metrics.RecordFinnhubSymbolsDeferred(deferred);
        if (deferred == 0)
            return basis;

        var selected = basis.Take(_maxSymbols).ToArray();
        _logger.LogWarning(
            "Finnhub の 1 巡回が巡回間隔に収まるのは {Max} 銘柄までのため、{Origin}の {Total} 銘柄のうち先頭の {Max} 銘柄だけを収集し、"
            + "{Deferred} 銘柄（{DeferredSymbols}）を後回しにします（計画 ADR-0043 決定2 (b)。自制レートか巡回間隔の見直しが要ります）。",
            _maxSymbols, origin, basis.Count, _maxSymbols, deferred, string.Join(",", basis.Skip(_maxSymbols)));
        return selected;
    }

    private static IReadOnlyList<string> UnitedStatesSymbols(IReadOnlyList<WatchedSymbol> watchlist) =>
        Distinct(watchlist.Where(w => w.Market == Market.UnitedStates).Select(w => w.Symbol).ToArray());

    // 前後空白を除き、空を捨て、重複は最初の 1 件を残す（大文字小文字を区別しない）。並びは保つ。
    private static IReadOnlyList<string> Distinct(IReadOnlyList<string> symbols)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(symbols.Count);
        foreach (var raw in symbols)
        {
            var symbol = raw?.Trim();
            if (!string.IsNullOrEmpty(symbol) && seen.Add(symbol))
                result.Add(symbol);
        }

        return result;
    }
}
