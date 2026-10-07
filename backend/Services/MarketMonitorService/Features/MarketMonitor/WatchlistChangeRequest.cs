using System.Text.Json;
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
    [property: JsonConverter(typeof(StrictMarketNameJsonConverter))] Market? Market,
    string Reason);

// FR-13, SC-02, IADR-0496（#1205 監査の追記）, #1192: market の厳格な変換器。文字列は**列挙名そのもの**（大小無視）だけを受ける。
// 🔴 JsonStringEnumConverter は Enum.Parse と同じく "Japan, UnitedStates"（ビット和＝UnitedStates）・前後の空白・数字の文字列
// （"1"）も受けるため、利用者が意図しない市場へ黙って登録され得た。数値（JSON の数）は従来どおり (Market)n へ写し、
// 未定義の値（例 99）は監視銘柄の検証（Enum.IsDefined）が 400 にする。null（省略を含む）は変換器を通らず従来どおり 400。
internal sealed class StrictMarketNameJsonConverter : JsonConverter<Market>
{
    public override Market Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.TryGetInt32(out var number)
                ? (Market)number
                : throw new JsonException("market の数値が範囲外です。");
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            foreach (var name in Enum.GetNames<Market>())
            {
                if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                {
                    return Enum.Parse<Market>(name);
                }
            }

            throw new JsonException("market は列挙名（Japan / UnitedStates）か数値で指定してください。");
        }

        throw new JsonException("market は列挙名（Japan / UnitedStates）か数値で指定してください。");
    }

    public override void Write(Utf8JsonWriter writer, Market value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}
