using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-02, FR-13, ADR-0044, #1134, IADR-0475: 定時サイクルの監視銘柄について、**このプロセスで権威源（市場監視）から直前に読めた一覧**を持つ。
// 権威源が読めないとき、供給口（HttpWatchlistProvider / GrpcWatchlistProvider）は構成の既定 watchlist へ倒さず、
// ここに直前の一覧があればそれを、無ければ null（不明＝そのサイクルの判断を見送る）を返す。
//
// 🔴 **singleton で登録する**（供給口はスコープごとに作られるため、直前の一覧は外に置かないと次のサイクルへ残らない）。
// 永続化はしない —— 再起動の後は不明から始まり、権威源が答えるまで判断しない（一斉再起動で構成の既定へ切り替わらない）。
// 警告は**障害ごとに 1 回**（読めた → 読めないへ変わったとき）、回復は Information で 1 回。照会そのものの失敗は供給口が照会ごとに残す。
public sealed class WatchlistLastKnown(ILogger<WatchlistLastKnown> logger)
{
    private readonly Lock _gate = new();
    private IReadOnlyList<WatchedSymbol>? _lastKnown;
    private bool _unavailable;

    /// <summary>権威源から読めた一覧（空を含む。空は「監視銘柄 0 件」という事実）を直前の値として覚える。</summary>
    public void Record(IReadOnlyList<WatchedSymbol> watchlist)
    {
        ArgumentNullException.ThrowIfNull(watchlist);

        bool recovered;
        lock (_gate)
        {
            _lastKnown = watchlist;
            recovered = _unavailable;
            _unavailable = false;
        }

        if (recovered)
            logger.LogInformation(
                "監視銘柄（watchlist）を権威源から再び読めました。{Count} 件で定時サイクルの判断を続けます。", watchlist.Count);
    }

    /// <summary>
    /// 権威源が読めなかった。直前に読めた一覧があればそれを、一度も読めていなければ <c>null</c>（不明）を返す。
    /// 🔴 構成の既定 watchlist は返さない。
    /// </summary>
    public IReadOnlyList<WatchedSymbol>? Unavailable()
    {
        IReadOnlyList<WatchedSymbol>? lastKnown;
        bool firstInOutage;
        lock (_gate)
        {
            lastKnown = _lastKnown;
            firstInOutage = !_unavailable;
            _unavailable = true;
        }

        if (!firstInOutage)
        {
            logger.LogDebug(
                "監視銘柄（watchlist）を権威源から引き続き読めません（直前の一覧: {State}）。",
                lastKnown is null ? "なし" : $"{lastKnown.Count} 件");
            return lastKnown;
        }

        if (lastKnown is null)
            logger.LogWarning(
                "監視銘柄（watchlist）を権威源から一度も読めていないため、不明として定時サイクルの判断を見送ります"
                + "（構成の既定 watchlist へは倒しません）。読めるまで毎サイクル見送ります。");
        else
            logger.LogWarning(
                "監視銘柄（watchlist）を権威源から読めないため、直前に読めた {Count} 件（{Symbols}）で定時サイクルの判断を続けます"
                + "（構成の既定 watchlist へは倒しません）。監視銘柄の変更は読めるまで反映されません。",
                lastKnown.Count, string.Join(",", lastKnown.Select(w => w.Symbol)));

        return lastKnown;
    }
}
