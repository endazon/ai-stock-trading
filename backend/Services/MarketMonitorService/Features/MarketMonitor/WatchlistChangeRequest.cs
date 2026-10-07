using System.Text.Json.Serialization;
using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Features.MarketMonitor;

// FR-13, UC-06: 監視銘柄の追加/削除の要求（理由必須・FR-11）。actor は要求本文ではなく認証済みトークンから取る。
// Market は nullable で受け、省略（null）を 400 に弾く（非 nullable だと省略時に既定値 0 へ暗黙バインドされるため）。
// **2 段目に残る**——追加（`AddWatchlistSymbol`）と削除（`RemoveWatchlistSymbol`）の 2 操作が使う
// （platform ADR-0068 決定2）。
// FR-13, SC-02, IADR-0496, #1192: market は列挙名の文字列（"UnitedStates"。大小無視）も受ける（数値 0/1 も従来どおり）。
// 🔴 変換器は**この要求型のプロパティにだけ**付ける。サービス全体（ConfigureHttpJsonOptions）へ足すと、読み取り口
// （GET /monitor/watchlist 等）の列挙が文字列で出て、数値で読む受け手（取引判断。T-10-931）が実行時に壊れる。
internal sealed record WatchlistChangeRequest(
    string Symbol,
    [property: JsonConverter(typeof(JsonStringEnumConverter<Market>))] Market? Market,
    string Reason);
