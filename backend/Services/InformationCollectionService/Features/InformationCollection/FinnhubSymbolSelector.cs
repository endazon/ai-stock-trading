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
// 4. 🔴 FR-01, FR-02, #1363, IADR-0547 決定 1: 監視銘柄に加えて**保有中の銘柄**（リスク管理の open-positions）を対象へ合流させる
//    （取引判断は「監視銘柄 ∪ 保有中の銘柄」を判断する。IADR-0521 決定 1）。保有を**先に**並べる —— 3. の上限は並びの先頭から
//    採るため、上限を超えたときは保有が先に採られる（監視銘柄の外の保有銘柄はその銘柄のニュースなしで出口を判断していた）。
//    保有を読めない巡回は監視銘柄だけから決め、警告する（IADR-0521 決定 1 と同じ縮退。直前の保有を使い続けない）。
//    保有は監視銘柄の照会に結線しているときだけ読む（未結線は従来どおり構成の固定リストだけ）。
//
// 集合は巡回ごとに 1 回だけ差し替える（参照の差し替えのみ。読み手はロックを取らない）。直前の値は**読めたときだけ**更新する。
public sealed class FinnhubSymbolSelector : IFinnhubSymbolSet, ICollectionTargetSymbols
{
    public const string OutcomeWatchlist = "watchlist";
    public const string OutcomeLastKnown = "last-known";
    public const string OutcomeConfiguredFallback = "configured-fallback";

    private readonly IWatchlistReader? _reader;
    private readonly IHeldSymbolsProvider? _held;
    private readonly IReadOnlyList<string> _configured;
    private readonly int _maxSymbols;
    private readonly BusinessMetrics _metrics;
    private readonly ILogger<FinnhubSymbolSelector> _logger;

    private IReadOnlyList<string> _current;
    private IReadOnlyList<string> _targets;
    private IReadOnlyList<string>? _lastKnown;

    /// <param name="reader">市場監視の読み口。<c>null</c>＝未結線（構成の固定リストを使い、照会しない）。</param>
    /// <param name="configured">構成の固定リスト（未結線時の対象・結線時のフォールバック）。</param>
    /// <param name="maxSymbolsPerCycle">1 巡回に収まる銘柄数（<see cref="FinnhubCycleFit.MaxSymbolsPerCycle"/>）。</param>
    /// <param name="held">リスク管理の保有の読み口。<c>null</c>＝未結線（保有を合流させない）。#1363, IADR-0547 決定 1。</param>
    public FinnhubSymbolSelector(
        IWatchlistReader? reader,
        IReadOnlyList<string> configured,
        int maxSymbolsPerCycle,
        BusinessMetrics metrics,
        ILogger<FinnhubSymbolSelector> logger,
        IHeldSymbolsProvider? held = null)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentOutOfRangeException.ThrowIfNegative(maxSymbolsPerCycle);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _reader = reader;
        _held = held;
        _configured = Distinct(configured);
        _maxSymbols = maxSymbolsPerCycle;
        _metrics = metrics;
        _logger = logger;

        // 最初の巡回の前（と未結線時のずっと）は構成の固定リスト。上限は同じく掛ける（ADR-0043 決定2 (b) は出所を問わない）。
        _targets = _configured;
        _current = Fit(_configured, "構成");
    }

    /// <summary>市場監視の監視銘柄に追随するか（結線されているか）。</summary>
    public bool FollowsWatchlist => _reader is not null;

    public IReadOnlyList<string> Current => Volatile.Read(ref _current);

    /// <summary>#1363, IADR-0547 決定 2: 上限を掛ける前の対象（保有が先、続いて監視銘柄。米国の銘柄）。</summary>
    public IReadOnlyList<string> UnitedStatesTargets => Volatile.Read(ref _targets);

    /// <summary>
    /// 監視銘柄を読み直して集合を差し替える（巡回の最初に 1 回呼ぶ）。未結線なら何もしない。
    /// 呼び出し側の中止以外の失敗は投げない（読めない＝不明として直前の値・構成へ倒す）。
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_reader is null)
            return;

        var read = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var held = await ReadHeldAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<string> watched;
        if (read is not null)
        {
            watched = UnitedStatesSymbols(read);
            _lastKnown = watched;
            _metrics.RecordFinnhubSymbolSetResolution(OutcomeWatchlist);
        }
        else if (_lastKnown is not null)
        {
            watched = _lastKnown;
            _metrics.RecordFinnhubSymbolSetResolution(OutcomeLastKnown);
            _logger.LogWarning(
                "市場監視の監視銘柄を読めなかったため、直前に読めた対象 {Count} 件（{Symbols}）で収集します"
                + "（不明を空と扱わない。監視銘柄の変更は読めるまで収集に反映されません）。",
                watched.Count, string.Join(",", watched));
        }
        else
        {
            watched = _configured;
            _metrics.RecordFinnhubSymbolSetResolution(OutcomeConfiguredFallback);
            _logger.LogWarning(
                "市場監視の監視銘柄を一度も読めていないため、構成の固定リスト（Collection:Source:Finnhub:Symbols）{Count} 件"
                + "（{Symbols}）で収集します（不明を空と扱わない）。",
                watched.Count, string.Join(",", watched));
        }

        // #1363, IADR-0547 決定 1: 保有を先に、続いて監視銘柄（重複は最初の 1 件＝保有の側）。
        var heldUs = held is null ? [] : UnitedStatesSymbols(held);
        var basis = Distinct([.. heldUs, .. watched]);
        var heldOnly = basis.Count - watched.Count;

        if (read is not null)
        {
            _logger.LogInformation(
                "監視銘柄から Finnhub の対象を決めました: 米国の銘柄 {UsCount} 件（監視銘柄 {Total} 件・監視銘柄の外の保有 {HeldOnly} 件・保有 {Held}）。",
                basis.Count, read.Count, heldOnly, HeldDescription(held, heldUs));
        }
        else if (heldOnly > 0)
        {
            _logger.LogInformation(
                "監視銘柄の代わりの対象に、監視銘柄の外の保有 {HeldOnly} 件を足しました（保有 {Held}）。",
                heldOnly, HeldDescription(held, heldUs));
        }

        Volatile.Write(ref _targets, basis);
        Volatile.Write(ref _current, Fit(basis, read is not null ? "監視銘柄と保有銘柄" : "代替と保有銘柄"));
    }

    // #1363, IADR-0547 決定 1: 保有を読む。未結線は null（合流させない・警告しない）。読めない（null）・例外は警告して null
    // （IADR-0521 決定 1・決定 5 と同じ規律: 不明を空と取り違えないよう警告し、この巡回は監視銘柄だけから決める）。中止は伝播する。
    private async Task<IReadOnlyList<WatchedSymbol>?> ReadHeldAsync(CancellationToken cancellationToken)
    {
        if (_held is null)
            return null;

        IReadOnlyList<WatchedSymbol>? held;
        try
        {
            held = await _held.GetHeldSymbolsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "保有銘柄の照会で例外が発生したため、この巡回は監視銘柄だけから収集の対象を決めます（IADR-0521 決定 1 と同じ縮退）。");
            return null;
        }

        if (held is null)
        {
            _logger.LogWarning(
                "保有銘柄を読めなかったため、この巡回は監視銘柄だけから収集の対象を決めます"
                + "（IADR-0521 決定 1 と同じ縮退。監視銘柄の外の保有銘柄のニュース・開示はこの巡回では集めません）。");
        }

        return held;
    }

    private static string HeldDescription(IReadOnlyList<WatchedSymbol>? held, IReadOnlyList<string> heldUs) =>
        held is null ? "不明または未結線" : $"米国 {heldUs.Count} 件";

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
