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
            // IADR-0035: 損切り幅が参照価格以上だと損切り価格が 0 以下（ロングでは損切り監視から外れる）になるため、
            // 異常値（幻覚）として Hold に倒す（損切り価格が権威データとして下流に渡るため下限を担保する）。
            if (dto.ReferencePrice <= 0m || dto.StopLossDistancePerShare <= 0m
                || dto.StopLossDistancePerShare >= dto.ReferencePrice)
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

    // 最初の '{' に対応する閉じ '}' までを、括弧の深さを数えて取り出す（前後の散文を除去）。
    // 文字列リテラル内の括弧・エスケープは無視するため、JSON の後ろに '}' を含む散文が続いても壊れない。
    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return text[start..(i + 1)];
                    }

                    break;
            }
        }

        return null; // 対応する閉じ括弧が見つからない
    }

    private sealed record DecisionDto(
        string? Action,
        string? Rationale,
        decimal ReferencePrice,
        decimal StopLossDistancePerShare);
}
