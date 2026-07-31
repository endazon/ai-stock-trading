using System.Globalization;

namespace AiStockTrading.Shared.Infrastructure.Composable.Llm;

// NFR（費用）, FR-04, IADR-0121 決定2/3: モデル別の単価表。**応答が名乗った実効モデル**から単価を引く。
// 用途別モデル割当（計画 ADR-0014 / MSP IADR-0112）で trade-decision=sonnet-5・report-*=fable-5/opus-5/sonnet-5 と
// モデルが混在したため、global 単一ペアでは実態と乖離する（#303）。ゲートウェイは越境ルーティング（ADR-0010）で
// 要求と異なるモデルを選び得るので、用途→単価の静的対応では追随できない。
//
// 単価の解析（InvariantCulture）と fail-safe を本型に閉じ込め、構成の読み出しだけを各サービスに残す
// （共有プロジェクトへ構成パッケージを持ち込まないため、単価は文字列で受ける）。
//
// fail-safe（IADR-0121 決定3・**「安全側 = 0」ではない**）:
//   1. 完全一致（大小無視）           → その単価
//   2. 未知・モデル名なし かつ 表が非空 → 表の**成分ごとの最大単価**
//   3. 表が空                         → 既定ペア（従来キー・未設定 0）
// 費用統制の危険側は**過小計上**である。未知モデルを 0 に倒すと月次上限（¥15,000）が構造的に効かなくなり、
// IADR-0114 決定6 が直した「毎回 ¥0 計上」が未知モデルの形で再発する。過大計上は統制が実態より早く効くだけ。
// 解決は例外を投げない（計測は best-effort＝LLM 応答を壊さない・IADR-0055）。
public sealed class LlmPriceTable
{
    private readonly IReadOnlyDictionary<string, LlmPrice> _perModel;
    private readonly LlmPrice _unknownModel;
    private readonly LlmPrice _fallback;

    private LlmPriceTable(IReadOnlyDictionary<string, LlmPrice> perModel, LlmPrice unknownModel, LlmPrice fallback)
    {
        _perModel = perModel;
        _unknownModel = unknownModel;
        _fallback = fallback;
    }

    /// <summary>
    /// モデル別単価表を組み立てる。単価は構成値そのまま（文字列）で受け、InvariantCulture で解析する。
    /// 解析できない・非正の単価を持つ行は**表に載せない**（→ 未知モデル扱い＝最大単価。誤設定を 0 円で通さない）。
    /// </summary>
    /// <param name="perModel">モデル ID と入出力単価（円/1k トークン）の並び。</param>
    /// <param name="defaultInputPer1kTokens">表が空のときに使う既定の入力単価（従来キー・未設定 0）。</param>
    /// <param name="defaultOutputPer1kTokens">表が空のときに使う既定の出力単価（従来キー・未設定 0）。</param>
    public static LlmPriceTable From(
        IEnumerable<(string Model, string? Input, string? Output)> perModel,
        string? defaultInputPer1kTokens = null,
        string? defaultOutputPer1kTokens = null)
    {
        var table = new Dictionary<string, LlmPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var (model, input, output) in perModel)
        {
            if (string.IsNullOrWhiteSpace(model))
                continue;
            if (ParsePricePer1k(input) is not { } inputPrice || ParsePricePer1k(output) is not { } outputPrice)
                continue;
            table[model.Trim()] = new LlmPrice(inputPrice, outputPrice);
        }

        // 未知モデルは成分ごとの最大へ倒す（入力が最大の行と出力が最大の行が別でも過小にしない）。
        var unknown = table.Count == 0
            ? LlmPrice.Zero
            : new LlmPrice(table.Values.Max(p => p.InputPer1kTokens), table.Values.Max(p => p.OutputPer1kTokens));

        // 既定ペアは入出力を独立に解析する（従来の ParsePricePer1k と同じ挙動＝片側だけの設定を壊さない）。
        var fallback = new LlmPrice(
            ParsePricePer1k(defaultInputPer1kTokens) ?? 0m,
            ParsePricePer1k(defaultOutputPer1kTokens) ?? 0m);

        return new LlmPriceTable(table, unknown, fallback);
    }

    /// <summary>実効モデル名から単価を引く。未知・null・空は安全側（過小計上を避ける側）へ倒す。</summary>
    public LlmPrice Resolve(string? model)
    {
        if (_perModel.Count == 0)
            return _fallback;

        return !string.IsNullOrWhiteSpace(model) && _perModel.TryGetValue(model.Trim(), out var price)
            ? price
            : _unknownModel;
    }

    // 単価の構成読み取り（円/1k トークン）。解析不能・非正値は null＝「設定されていない」として扱う。
    private static decimal? ParsePricePer1k(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) && price > 0m
            ? price
            : null;
}
