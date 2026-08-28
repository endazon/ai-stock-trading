using MarketMonitorService.Application.Ports;
using MarketMonitorService.Application.State;
using MarketMonitorService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Application.Services;

// FR-03, FR-11, FR-13, UC-06: 監視銘柄（watchlist）の取得・追加・削除。変更（追加・削除）は利用者のみ（アクター・理由必須）。
// 変更は前後値つきで履歴に記録する（FR-13。変更は利用者のみ・変更履歴を記録する）。Risk の RiskSettingsService を
// ミラーする。永続化は IMonitoredSymbolStore の単一行 JSON＋Version（楽観排他・IADR-0012 踏襲）に委ね、
// 競合はホスト層の例外フィルタで 409 に写像する。変更（Add/Remove）は生成AI・自動処理から呼べない（ホスト層 owner サブグループ
// OwnerOnly で担保）。取得（GetWatchlist）はホスト層 read サブグループ OwnerOrService で定時サイクル（#11）にも開放する（FR-02, IADR-0095）。
public sealed class MonitorWatchlistService(
    IMonitoredSymbolStore store,
    IMonitorSettingsChangeLog changeLog,
    IClock clock)
{
    public IReadOnlyCollection<MonitoredSymbol> GetWatchlist() => store.GetSettings().MonitoredSymbols;

    public IReadOnlyCollection<MonitoredSymbol> Add(string symbol, Market market, string actor, string reason)
    {
        RequireActorAndReason(actor, reason);
        var target = Normalize(symbol, market);

        var current = store.GetSettings();
        var symbols = current.MonitoredSymbols;
        // FR-13: 重複追加は検証エラー（400）。楽観排他競合（409）とは区別する。
        if (symbols.Any(s => Same(s, target)))
        {
            throw new ArgumentException($"銘柄 {target.Symbol}（{market}）は既に監視対象です。", nameof(symbol));
        }

        var updated = current with { MonitoredSymbols = [.. symbols, target] };
        Save(current.MonitoredSymbols, updated, MonitorSettingsChangeType.WatchlistSymbolAdded, actor, reason);
        return updated.MonitoredSymbols;
    }

    public IReadOnlyCollection<MonitoredSymbol> Remove(string symbol, Market market, string actor, string reason)
    {
        RequireActorAndReason(actor, reason);
        var target = Normalize(symbol, market);

        var current = store.GetSettings();
        var remaining = current.MonitoredSymbols.Where(s => !Same(s, target)).ToList();
        // FR-13: 不在銘柄の削除は検証エラー（400）。写像の単一情報源を保つため 404/409 は新設しない（IADR-0088）。
        if (remaining.Count == current.MonitoredSymbols.Count)
        {
            throw new ArgumentException($"銘柄 {target.Symbol}（{market}）は監視対象にありません。", nameof(symbol));
        }

        var updated = current with { MonitoredSymbols = remaining };
        Save(current.MonitoredSymbols, updated, MonitorSettingsChangeType.WatchlistSymbolRemoved, actor, reason);
        return updated.MonitoredSymbols;
    }

    public IReadOnlyList<MonitorSettingsChangeEntry> GetHistory() => changeLog.GetHistory();

    private void Save(
        IReadOnlyCollection<MonitoredSymbol> before,
        MarketMonitorSettings updated,
        MonitorSettingsChangeType changeType,
        string actor,
        string reason)
    {
        // 永続化を先に確定させる（fail-safe）。Version 楽観排他競合はここで DbUpdateConcurrencyException として送出され、
        // ホスト層で 409 に写像される（履歴は記録されない＝台帳と履歴が食い違わない）。
        store.Save(updated);
        changeLog.Record(new MonitorSettingsChangeEntry(
            actor, changeType, reason, clock.UtcNow,
            Before: Render(before), After: Render(updated.MonitoredSymbols)));
    }

    // 監視銘柄を検証・正規化する。空 symbol・未定義 market は検証エラー（400）。
    private static MonitoredSymbol Normalize(string symbol, Market market)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (!Enum.IsDefined(market))
        {
            throw new ArgumentException($"市場 {market} は未定義です。", nameof(market));
        }

        return new MonitoredSymbol(symbol.Trim(), market);
    }

    // (Symbol, Market) の一致判定。銘柄コードは大文字小文字を無視して重複・不在を突き合わせる。
    private static bool Same(MonitoredSymbol a, MonitoredSymbol b) =>
        a.Market == b.Market && string.Equals(a.Symbol, b.Symbol, StringComparison.OrdinalIgnoreCase);

    // 履歴の前後値レンダリング（SYMBOL@Market をカンマ区切り）。
    private static string Render(IReadOnlyCollection<MonitoredSymbol> symbols) =>
        symbols.Count == 0 ? "(なし)" : string.Join(", ", symbols.Select(s => $"{s.Symbol}@{s.Market}"));

    private static void RequireActorAndReason(string actor, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
    }
}
