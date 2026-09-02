using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Features.MarketMonitor;

// FR-13, UC-06: 監視銘柄の追加/削除の要求（理由必須・FR-11）。actor は要求本文ではなく認証済みトークンから取る。
// Market は nullable で受け、省略（null）を 400 に弾く（非 nullable だと省略時に既定値 0 へ暗黙バインドされるため）。
// **2 段目に残る**——追加（`AddWatchlistSymbol`）と削除（`RemoveWatchlistSymbol`）の 2 操作が使う
// （platform ADR-0068 決定2）。
internal sealed record WatchlistChangeRequest(string Symbol, Market? Market, string Reason);
