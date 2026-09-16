using System.Text.Json;

namespace TradeDecisionService.Domain;

// FR-04, FR-11, #337（#290 吸収）, IADR-0248: LLM の JSON 構造化出力を LlmDecision に解析する。LLM 出力は前後に
// 散文を含み得るため、最初の JSON オブジェクト（{...}）を抽出して解析する。
//
// 🔴 **「解析不能」と「見送り（Hold）」は別の事実である**（#290 の再発防止）。どちらも安全側の挙動は
// 同じ（取引しない）だが、前者は**出力の形の問題**（プロンプト・モデルの退行を示す信号）、後者は
// **LLM の判断そのもの**（設計上の正常な結果）であり、混同すると退行が「見送りが増えた」として
// 監査から見えなくなる。区別は ParseDetailed が返す ParseFailure が持ち、挙動（Hold に倒す）は変えない。
public static class TradeDecisionParser
{

    /// <summary>互換 API。解析不能・不正出力は安全側で Hold（取引しない）に倒す（従来どおり）。</summary>
    public static LlmDecision Parse(string? llmOutput) => ParseDetailed(llmOutput).Decision;

    /// <summary>
    /// #290, IADR-0248: 解析結果と失敗種別を区別して返す。<c>Failure</c> が非 null のとき Decision は
    /// 常に Hold（安全既定）であり、<b>「LLM が見送りを選んだ」のではなく「出力を解析できなかった」</b>ことを表す。
    /// </summary>
    public static ParsedTradeDecision ParseDetailed(string? llmOutput)
    {
        var envelope = ReadEnvelope(llmOutput);
        if (envelope.Failure is { } failure)
        {
            return ParsedTradeDecision.Failed(failure.Kind, failure.Detail);
        }

        var dto = envelope.Dto!;
        var action = envelope.Action;
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
        // #785: null（未供給）も「成立しない」に含める（Buy/Sell で数値が無ければサイジング不能）。
        // #806: この不変量は**二次本判断（サイジングへ渡す）だけ**のもの。一次スクリーニングは ParseScreening で
        // 方向だけを読み、ここへは来ない。
        if (dto.ReferencePrice is not > 0m || dto.StopLossDistancePerShare is not > 0m
            || dto.StopLossDistancePerShare >= dto.ReferencePrice)
        {
            return ParsedTradeDecision.Failed(
                TradeDecisionParseFailureKind.InvalidValues,
                $"価格・損切り幅が不正: referencePrice={dto.ReferencePrice} stopLossDistance={dto.StopLossDistancePerShare}");
        }

        // FR-17, IADR-0076: 想定利益（任意）。欠損は 0、負値は 0 に正規化する（保守側＝採算ゲート有効時は Hold に倒れる）。
        var expectedProfit = dto.ExpectedProfitPerShare is > 0m ? dto.ExpectedProfitPerShare.Value : 0m;
        return ParsedTradeDecision.Ok(new LlmDecision(
            action, dto.Rationale ?? string.Empty, dto.ReferencePrice.Value, dto.StopLossDistancePerShare.Value, expectedProfit));
    }

    /// <summary>
    /// FR-04, FR-11, #806, IADR-0039, IADR-0248: 一次スクリーニング用の解釈。<b>方向（関心の有無）だけ</b>を読む。
    /// 価格・損切り幅は二次本判断が改めて出すため、Buy/Sell の数値欠損・不変量違反は見ない（<c>InvalidValues</c> は出ない）。
    /// Hold は数値の有無を問わず見送り。<c>Failure</c> が付くのは出力の形の問題（空・JSON なし・JSON 不正・action 不明）だけで、
    /// #290 の「解析不能と見送りの区別」はそのまま保つ。
    /// </summary>
    public static ParsedScreening ParseScreening(string? llmOutput)
    {
        var envelope = ReadEnvelope(llmOutput);
        if (envelope.Failure is { } failure)
        {
            return ParsedScreening.Failed(failure);
        }

        var dto = envelope.Dto!;
        var action = envelope.Action;
        var rationale = action == TradeAction.Hold
            ? dto.Rationale ?? LlmDecision.Hold.Rationale
            : dto.Rationale ?? string.Empty;
        return ParsedScreening.Ok(action, rationale);
    }

    // #806: 一次・二次で共通の前段（空出力／JSON 抽出／JSON 構文／action）。ここで付く Failure はすべて
    // 「出力の形の問題」であり、値の不変量（InvalidValues）は二次本判断（ParseDetailed）だけが後段で見る。
    private static Envelope ReadEnvelope(string? llmOutput)
    {
        if (string.IsNullOrWhiteSpace(llmOutput))
        {
            return Envelope.Failed(TradeDecisionParseFailureKind.EmptyOutput, "出力が空");
        }

        var json = ExtractJsonObject(llmOutput);
        if (json is null)
        {
            return Envelope.Failed(TradeDecisionParseFailureKind.NoJsonObject, "JSON オブジェクトを抽出できない");
        }

        try
        {
            var dto = ReadDto(json);
            if (dto is null)
            {
                return Envelope.Failed(TradeDecisionParseFailureKind.MalformedJson, "JSON がオブジェクトでない");
            }

            if (!TryParseAction(dto.Action, out var action))
            {
                return Envelope.Failed(TradeDecisionParseFailureKind.UnknownAction, $"action が不明: {dto.Action}");
            }

            return new Envelope(dto, action, null);
        }
        catch (JsonException ex)
        {
            return Envelope.Failed(TradeDecisionParseFailureKind.MalformedJson, ex.Message);
        }
    }

    // 前段の読み取り結果。Failure が null のとき Dto は非 null で Action は Buy/Sell/Hold のいずれか。
    private readonly record struct Envelope(DecisionDto? Dto, TradeAction Action, TradeDecisionParseFailure? Failure)
    {
        public static Envelope Failed(TradeDecisionParseFailureKind kind, string detail) =>
            new(null, TradeAction.Hold, new TradeDecisionParseFailure(kind, detail));
    }

    // #785: 型付き Deserialize は数値項目の null / 非数値で action を読む前に落ちる。項目ごとに寛容に読み、
    // 数値は「数値・数値文字列なら値、それ以外は null（未供給）」とする。未供給の扱いは呼び出し側が action で決める。
    private static DecisionDto? ReadDto(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var root = doc.RootElement;
        return new DecisionDto(
            ReadString(root, "action"),
            ReadString(root, "rationale"),
            ReadDecimal(root, "referencePrice"),
            ReadDecimal(root, "stopLossDistancePerShare"),
            ReadDecimal(root, "expectedProfitPerShare"));
    }

    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string? ReadString(JsonElement obj, string name)
        => TryGet(obj, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal? ReadDecimal(JsonElement obj, string name)
    {
        if (!TryGet(obj, name, out var v))
        {
            return null;
        }
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDecimal(out var d) => d,
            JsonValueKind.String when decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
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

    // #785: 数値は null 許容（Hold のときモデルは null を返してよい）。
    // #806: Buy/Sell で必須なのは二次本判断（ParseDetailed。無ければ InvalidValues）だけ。一次（ParseScreening）は見ない。
    private sealed record DecisionDto(
        string? Action,
        string? Rationale,
        decimal? ReferencePrice,
        decimal? StopLossDistancePerShare,
        decimal? ExpectedProfitPerShare);
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

/// <summary>
/// FR-04, FR-11, #806, IADR-0248: 一次スクリーニングの解釈結果。方向（<see cref="Action"/>）と根拠だけを持ち、価格・損切り幅は持たない
/// （二次本判断が出す）。<see cref="Failure"/> が非 null のとき <see cref="Action"/> は Hold（安全既定）で、それは<b>解析不能</b>を意味する。
/// </summary>
public sealed record ParsedScreening(TradeAction Action, string Rationale, TradeDecisionParseFailure? Failure)
{
    /// <summary>解析不能（出力の形の問題）か。false のとき Action は LLM の判断そのもの。</summary>
    public bool IsUnparseable => Failure is not null;

    /// <summary>関心あり（Buy/Sell）＝二次本判断へ進める。Hold と解析不能はいずれも false（打ち切り）。</summary>
    public bool IsInterested => Failure is null && Action != TradeAction.Hold;

    /// <summary>打ち切り時に下流へ渡す Hold（根拠つき）。解析不能なら安全既定の <see cref="LlmDecision.Hold"/>。</summary>
    public LlmDecision AsHold => Failure is null ? LlmDecision.Hold with { Rationale = Rationale } : LlmDecision.Hold;

    public static ParsedScreening Ok(TradeAction action, string rationale) => new(action, rationale, null);

    public static ParsedScreening Failed(TradeDecisionParseFailure failure) =>
        new(TradeAction.Hold, LlmDecision.Hold.Rationale, failure);
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
