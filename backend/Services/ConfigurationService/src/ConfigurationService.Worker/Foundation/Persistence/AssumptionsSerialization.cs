using System.Text.Json;
using AiStockTrading.Configuration.Domain;

namespace AiStockTrading.Configuration.Worker.Foundation.Persistence;

// IADR-0021: TradingAssumptions の JSON 直列化。すべて具象レコード/数値のため標準の Web 既定で往復可能。
internal static class AssumptionsSerialization
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(TradingAssumptions assumptions) =>
        JsonSerializer.Serialize(assumptions, Options);

    public static TradingAssumptions Deserialize(string json) =>
        JsonSerializer.Deserialize<TradingAssumptions>(json, Options)
        ?? throw new InvalidOperationException("全体前提条件の JSON を逆直列化できませんでした。");
}
