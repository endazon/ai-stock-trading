using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiStockTrading.Shared.Contracts.Backtest;

// FR-15, ADR-0033 決定2, IADR-0318: 記録集合の直列化オプション（記録側と再生側で**同一の設定を共有する**）。
//
// 🔴 オプションを両サービスに複製しない。列挙の表現（数値か文字列か）が片側だけ変わると、
// 記録は読めるのに `Hold` が `Buy` に化ける形で壊れる（数値表現は要素の並び替えに耐えない）。
public static class Stage0DecisionRecordJson
{
    /// <summary>
    /// 記録の JSON オプション。**列挙は文字列**（記録は長期に残り、enum の宣言順が変わっても意味が動かない形にする）。
    /// インデントは付けない（記録は機械が読む。差分を人が読む場面は想定しない）。
    /// </summary>
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize(Stage0DecisionRecordSet recordSet)
    {
        ArgumentNullException.ThrowIfNull(recordSet);
        return JsonSerializer.Serialize(recordSet, Options);
    }

    /// <summary>解釈できない JSON は例外にせず null を返す（呼び出し元は「記録なし」として fail-closed へ倒す）。</summary>
    public static Stage0DecisionRecordSet? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Stage0DecisionRecordSet>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
