using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiStockTrading.Audit.Application.Services;

// FR-11, IADR-0019: 監査記録 Detail のシリアライズ方針。列挙は文字列化して人間可読・監査容易にする。
internal static class AuditSerialization
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
