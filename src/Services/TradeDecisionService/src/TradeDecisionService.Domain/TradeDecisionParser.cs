using System.Text.Json;

namespace AiStockTrading.TradeDecision.Domain;

// FR-04, FR-11: LLM の JSON 構造化出力を LlmDecision に解析する。LLM 出力は前後に散文を含み得るため、最初の
// JSON オブジェクト（{...}）を抽出して解析する。不正・欠損・未知の action は安全側で Hold（取引しない）に倒す。
public static class TradeDecisionParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static LlmDecision Parse(string? llmOutput)
    {
        if (string.IsNullOrWhiteSpace(llmOutput))
        {
            return LlmDecision.Hold;
        }

        var json = ExtractJsonObject(llmOutput);
        if (json is null)
        {
            return LlmDecision.Hold;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<DecisionDto>(json, Options);
            if (dto is null || !TryParseAction(dto.Action, out var action))
            {
                return LlmDecision.Hold;
            }

            if (action == TradeAction.Hold)
            {
                return LlmDecision.Hold with { Rationale = dto.Rationale ?? LlmDecision.Hold.Rationale };
            }

            // Buy/Sell は価格・損切り幅が正でなければサイジング不能のため Hold に倒す。
            if (dto.ReferencePrice <= 0m || dto.StopLossDistancePerShare <= 0m)
            {
                return LlmDecision.Hold;
            }

            return new LlmDecision(action, dto.Rationale ?? string.Empty, dto.ReferencePrice, dto.StopLossDistancePerShare);
        }
        catch (JsonException)
        {
            return LlmDecision.Hold;
        }
    }

    private static bool TryParseAction(string? value, out TradeAction action)
    {
        action = TradeAction.Hold;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Enum.TryParse(value.Trim(), ignoreCase: true, out action)
            && Enum.IsDefined(action);
    }

    // 最初の '{' から対応する最後の '}' までを取り出す（前後の散文を除去）。
    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private sealed record DecisionDto(
        string? Action,
        string? Rationale,
        decimal ReferencePrice,
        decimal StopLossDistancePerShare);
}
