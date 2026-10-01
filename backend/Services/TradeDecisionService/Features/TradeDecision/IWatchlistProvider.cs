using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision;

// FR-02, IADR-0023/0095: 定時サイクルで評価する監視銘柄の供給。権威源は市場監視（#10 MarketMonitor の MonitoredSymbols）。
// 供給は s2s 同期照会（IADR-0095）のため非同期ポートとする。権威源が未結線なら構成ベース（後方互換）。
public interface IWatchlistProvider
{
    // FR-02, ADR-0044, #1134, IADR-0475: 定時サイクルの判断対象。権威源から読めればその一覧（空＝0 件）、読めなければ
    // **このプロセスで直前に読めた一覧**、一度も読めていなければ **null（不明）** を返す。null のサイクルは判断しない。
    // 🔴 照会に失敗しても**構成の既定 watchlist へは倒さない**（一斉再起動の直後に判断対象が構成の銘柄へ切り替わらない）。
    Task<IReadOnlyList<WatchedSymbol>?> GetWatchlistAsync(CancellationToken cancellationToken = default);

    // FR-04, #1034, IADR-0440 決定 2: 判断のプロンプトへ載せる監視銘柄。**権威源（市場監視）から読めたときだけ**一覧を返し、
    // 読めない（非 2xx・タイムアウト・例外・不正応答）・権威源が未結線のときは **null（不明）** を返す。
    // 🔴 上の GetWatchlistAsync と違い**構成の固定リストへ倒さない** —— 倒した一覧をプロンプトへ載せると、古いかもしれない
    // 一覧を「判断時点の監視銘柄」として LLM に読ませることになる（不明を不明と書けない）。
    Task<IReadOnlyList<WatchedSymbol>?> GetAuthoritativeWatchlistAsync(CancellationToken cancellationToken = default);
}

// 監視銘柄（銘柄コード・市場）。
public sealed record WatchedSymbol(string Symbol, Market Market);
