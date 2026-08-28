using System.Text.Json;

namespace AiStockTrading.TradeDecision.Domain;

// FR-04, FR-11, #337（#290 吸収）, IADR-0248: LLM の JSON 構造化出力を LlmDecision に解析する。LLM 出力は前後に
// 散文を含み得るため、最初の JSON オブジェクト（{...}）を抽出して解析する。
//
// 🔴 **「解析不能」と「見送り（Hold）」は別の事実である**（#290 の再発防止）。どちらも安全側の挙動は
// 同じ（取引しない）だが、前者は**出力の形の問題**（プロンプト・モデルの退行を示す信号）、後者は
// **LLM の判断そのもの**（設計上の正常な結果）であり、混同すると退行が「見送りが増えた」として
// 監査から見えなくなる。区別は ParseDetailed が返す ParseFailure が持ち、挙動（Hold に倒す）は変えない。
public static class TradeDecisionParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>互換 API。解析不能・不正出力は安全側で Hold（取引しない）に倒す（従来どおり）。</summary>
    public static LlmDecision Parse(string? llmOutput) => ParseDetailed(llmOutput).Decision;

    /// <summary>
    /// #290, IADR-0248: 解析結果と失敗種別を区別して返す。<c>Failure</c> が非 null のとき Decision は
    /// 常に Hold（安全既定）であり、<b>「LLM が見送りを選んだ」のではなく「出力を解析できなかった」</b>ことを表す。
    /// </summary>
    public static ParsedTradeDecision ParseDetailed(string? llmOutput)
    {
        if (string.IsNullOrWhiteSpace(llmOutput))
        {
            return ParsedTradeDecision.Failed(TradeDecisionParseFailureKind.EmptyOutput, "出力が空");
        }

        var json = ExtractJsonObject(llmOutput);
        if (json is null)
        {
            return ParsedTradeDecision.Failed(
                TradeDecisionParseFailureKind.NoJsonObject, "JSON オブジェクトを抽出できない");
        }

        try
        {
            var dto = JsonSerializer.Deserialize<DecisionDto>(json, Options);
            if (dto is null)
            {
                return ParsedTradeDecision.Failed(TradeDecisionParseFailureKind.MalformedJson, "JSON が null");
            }

            if (!TryParseAction(dto.Action, out var action))
            {
                return ParsedTradeDecision.Failed(
                    TradeDecisionParseFailureKind.UnknownAction, $"action が不明: {dto.Action}");
            }

            if (action == TradeAction.Hold)
            {
                // 解析できた上での見送り（LLM の判断）。Failure は付かない。
                return ParsedTradeDecision.Ok(
                    LlmDecision.Hold with { Rationale = dto.Rationale ?? LlmDecision.Hold.Rationale });
            }

            // Buy/Sell は価格・損切り幅が正でなければサイジング不能のため Hold に倒す。
            // IADR-0035: 損切り幅が参照価格以上だと損切り価格が 0 以下（ロングでは損切り監視から外れる）になるため、
            // 異常値（幻覚）として Hold に倒す（損切り価格が権威データとして下流に渡るため下限を担保する）。
            // #290: これは「解析はできたが値が成立しない」＝解析不能系（InvalidValues）として区別する。
            if (dto.ReferencePrice <= 0m || dto.StopLossDistancePerShare <= 0m
                || dto.StopLossDistancePerShare >= dto.ReferencePrice)
            {
                return ParsedTradeDecision.Failed(
                    TradeDecisionParseFailureKind.InvalidValues,
                    $"価格・損切り幅が不正: referencePrice={dto.ReferencePrice} stopLossDistance={dto.StopLossDistancePerShare}");
            }

            // FR-17, IADR-0076: 想定利益（任意）。欠損は 0、負値は 0 に正規化する（保守側＝採算ゲート有効時は Hold に倒れる）。
            var expectedProfit = dto.ExpectedProfitPerShare > 0m ? dto.ExpectedProfitPerShare : 0m;
            return ParsedTradeDecision.Ok(new LlmDecision(
                action, dto.Rationale ?? string.Empty, dto.ReferencePrice, dto.StopLossDistancePerShare, expectedProfit));
        }
        catch (JsonException ex)
        {
            return ParsedTradeDecision.Failed(TradeDecisionParseFailureKind.MalformedJson, ex.Message);
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
        decimal StopLossDistancePerShare,
        decimal ExpectedProfitPerShare);
}

/// <summary>
/// #290, IADR-0248: 解析結果。<see cref="Failure"/> が非 null のとき <see cref="Decision"/> は常に Hold
/// （安全既定・取引しない）であり、それは<b>解析不能</b>を意味する——LLM が選んだ見送りではない。
/// </summary>
public sealed record ParsedTradeDecision(LlmDecision Decision, TradeDecisionParseFailure? Failure)
{
    /// <summary>解析不能（出力の形の問題）か。false のとき Decision は LLM の判断そのもの。</summary>
    public bool IsUnparseable => Failure is not null;

    public static ParsedTradeDecision Ok(LlmDecision decision) => new(decision, null);

    public static ParsedTradeDecision Failed(TradeDecisionParseFailureKind kind, string detail) =>
        new(LlmDecision.Hold, new TradeDecisionParseFailure(kind, detail));
}

/// <summary>#290: 解析失敗の種別（FR-11 の記録用。挙動はいずれも Hold＝取引しない）。</summary>
public enum TradeDecisionParseFailureKind
{
    /// <summary>出力が空・空白のみ。</summary>
    EmptyOutput,

    /// <summary>JSON オブジェクトを抽出できない（散文のみ・括弧が閉じない）。</summary>
    NoJsonObject,

    /// <summary>JSON として不正（構文エラー・null）。</summary>
    MalformedJson,

    /// <summary>action が欠損・未知の値。</summary>
    UnknownAction,

    /// <summary>解析はできたが値が成立しない（価格・損切り幅の不変量違反＝幻覚の疑い）。</summary>
    InvalidValues,
}

/// <summary>#290: 解析失敗の記録（種別と機械可読な詳細）。</summary>
public sealed record TradeDecisionParseFailure(TradeDecisionParseFailureKind Kind, string Detail);
