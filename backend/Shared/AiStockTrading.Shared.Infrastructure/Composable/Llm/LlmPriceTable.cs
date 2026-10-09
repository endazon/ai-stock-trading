using System.Globalization;

namespace AiStockTrading.Shared.Infrastructure.Composable.Llm;

// NFR（費用）, FR-04, IADR-0122 決定2/3: モデル別の単価表。**応答が名乗った実効モデル**から単価を引く。
// 用途別モデル割当（計画 ADR-0014 / MSP/IADR-0112）で trade-decision=sonnet・report-*=opus/sonnet/haiku と
// モデルが混在したため、global 単一ペアでは実態と乖離する（#303）。ゲートウェイは越境ルーティング（ADR-0010）で
// 要求と異なるモデルを選び得るので、用途→単価の静的対応では追随できない。
//
// 単価の解析（InvariantCulture）と fail-safe を本型に閉じ込め、構成の読み出しだけを各サービスに残す
// （共有プロジェクトへ構成パッケージを持ち込まないため、単価は文字列で受ける）。
//
// fail-safe（IADR-0122 決定3・**「安全側 = 0」ではない**）:
//   1. 一致（大小無視・`-` と `_` を同一視） → その単価
//   2. 未知・モデル名なし かつ 表が非空 → 表の**成分ごとの最大単価**
//   3. 表が空                         → 既定ペア（従来キー・未設定 0）
// 費用統制の危険側は**過小計上**である。未知モデルを 0 に倒すと月次上限（¥15,000）が構造的に効かなくなり、
// IADR-0114 決定6 が直した「毎回 ¥0 計上」が未知モデルの形で再発する。過大計上は統制が実態より早く効くだけ。
// 解決は例外を投げない（計測は best-effort＝LLM 応答を壊さない・IADR-0055）。
//
// #817（IADR-0122 2026-09-17 追記）: 単価は env 名 `LlmPricing__PerModel__<model>__*` で注入されるが、イメージの
// `sh -c` 起動（dash）は `-` を含む（シェル識別子でない）env 名を exec 先へ渡さない。稼働では表が空のまま全呼び出しが
// 0 円計上になっていた。env 名はモデル ID の `-` を `_` で書き（`claude_sonnet_5_5`）、照合は双方を正規化して同一視する。
//
// FR-04, NFR（費用）, #1295, IADR-0524: **プロンプト長による 2 段の単価**（`claude-haiku-5-5` は入力 100,000 トークン以下と超で
// 単価が違う。提供元の公表値 2026-10-10・planning#783）。行に任意の第 2 段（`LongContextThresholdTokens` と第 2 の単価ペア）を
// 持たせ、**計上時の入力トークン数**が閾値を超えた要求だけ第 2 段で引く（要求ごとに決まる）。上限側へ寄せた 1 段にしないのは、
// 常時の過大計上が月次上限を実態より早く効かせ続けるため（ADR-0037 決定1 の趣旨）。第 2 段を持たない行・未知モデルの扱いは従来どおり。
public sealed class LlmPriceTable
{
    private readonly IReadOnlyDictionary<string, LlmPriceEntry> _perModel;
    private readonly LlmPrice _unknownModel;
    private readonly LlmPrice _fallback;

    private LlmPriceTable(IReadOnlyDictionary<string, LlmPriceEntry> perModel, LlmPrice unknownModel, LlmPrice fallback)
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
        ArgumentNullException.ThrowIfNull(perModel);
        return FromRows(
            perModel.Select(row => new LlmPriceRow(row.Model, row.Input, row.Output)),
            defaultInputPer1kTokens,
            defaultOutputPer1kTokens);
    }

    /// <summary>
    /// モデル別単価表を組み立てる（第 2 段＝プロンプト長の上位段を持てる形。#1295, IADR-0524）。
    /// 第 2 段のキーが 1 つでも書かれていて 3 つ揃って解析できない行は**表に載せない**（→ 未知モデル扱い＝最大単価。
    /// 第 2 段の誤設定で長いプロンプトを第 1 段の安い単価で通さない）。
    /// </summary>
    public static LlmPriceTable FromRows(
        IEnumerable<LlmPriceRow> perModel,
        string? defaultInputPer1kTokens = null,
        string? defaultOutputPer1kTokens = null)
    {
        ArgumentNullException.ThrowIfNull(perModel);
        var table = new Dictionary<string, LlmPriceEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in perModel)
        {
            if (string.IsNullOrWhiteSpace(row.Model))
                continue;
            if (ParsePricePer1k(row.Input) is not { } inputPrice || ParsePricePer1k(row.Output) is not { } outputPrice)
                continue;
            if (!TryParseLongContext(row, out var longContext))
                continue;
            table[Normalize(row.Model)] = new LlmPriceEntry(new LlmPrice(inputPrice, outputPrice), longContext);
        }

        // 未知モデルは成分ごとの最大へ倒す（入力が最大の行と出力が最大の行が別でも過小にしない）。
        // 第 2 段も母集合に含める（第 2 段だけが最大でも過小にしない）。
        var every = table.Values
            .SelectMany(e => e.LongContext is { } tier ? new[] { e.Base, tier.Price } : [e.Base])
            .ToArray();
        var unknown = every.Length == 0
            ? LlmPrice.Zero
            : new LlmPrice(every.Max(p => p.InputPer1kTokens), every.Max(p => p.OutputPer1kTokens));

        // 既定ペアは入出力を独立に解析する（従来の ParsePricePer1k と同じ挙動＝片側だけの設定を壊さない）。
        var fallback = new LlmPrice(
            ParsePricePer1k(defaultInputPer1kTokens) ?? 0m,
            ParsePricePer1k(defaultOutputPer1kTokens) ?? 0m);

        return new LlmPriceTable(table, unknown, fallback);
    }

    /// <summary>
    /// 単価が実質 0 か（モデル別の表が空 かつ 既定ペアも入出力とも 0）。真なら全呼び出しが 0 円で計上される。
    /// 解決は変えない（0 は IADR-0055 の無害な fail-safe）。起動時の判定（#817 の警告・#1197 / IADR-0499 の配備での起動拒否。
    /// <see cref="LlmPricingStartupGuard"/>）にだけ使う。
    /// </summary>
    public bool IsEffectivelyZero => _perModel.Count == 0 && _fallback == LlmPrice.Zero;

    /// <summary>
    /// 実効モデル名から単価を引く。未知・null・空は安全側（過小計上を避ける側）へ倒す。
    /// 第 2 段を持つ行は**第 1 段**（閾値以下）を返す —— 入力トークン数が分かる計上では
    /// <see cref="Resolve(string?, int)"/> を使う。
    /// </summary>
    public LlmPrice Resolve(string? model) => Resolve(model, inputTokens: 0);

    /// <summary>
    /// 実効モデル名と**その要求の入力トークン数**から単価を引く（#1295, IADR-0524）。入力トークン数が行の閾値を
    /// 超えれば第 2 段、それ以外は第 1 段。第 2 段を持たない行・未知モデル・表が空のときは入力トークン数に依らない。
    /// </summary>
    public LlmPrice Resolve(string? model, int inputTokens)
    {
        if (_perModel.Count == 0)
            return _fallback;

        if (string.IsNullOrWhiteSpace(model) || !_perModel.TryGetValue(Normalize(model), out var entry))
            return _unknownModel;

        return entry.LongContext is { } tier && inputTokens > tier.ThresholdTokens
            ? tier.Price
            : entry.Base;
    }

    // #817: モデル ID の `_` と `-` を同一視する（env 名はシェル識別子に `-` を書けない）。大小は辞書の比較器が無視する。
    private static string Normalize(string model) => model.Trim().Replace('_', '-');

    // 第 2 段の読み取り。3 キーとも空＝第 2 段なし（true・null）。1 つでも書かれていれば 3 つとも解析できるときだけ true。
    private static bool TryParseLongContext(LlmPriceRow row, out LlmLongContextPrice? longContext)
    {
        longContext = null;
        if (string.IsNullOrWhiteSpace(row.LongContextThresholdTokens)
            && string.IsNullOrWhiteSpace(row.LongContextInput)
            && string.IsNullOrWhiteSpace(row.LongContextOutput))
            return true;

        if (!int.TryParse(row.LongContextThresholdTokens, NumberStyles.Integer, CultureInfo.InvariantCulture, out var threshold)
            || threshold <= 0
            || ParsePricePer1k(row.LongContextInput) is not { } input
            || ParsePricePer1k(row.LongContextOutput) is not { } output)
            return false;

        longContext = new LlmLongContextPrice(threshold, new LlmPrice(input, output));
        return true;
    }

    // 単価の構成読み取り（円/1k トークン）。解析不能・非正値は null＝「設定されていない」として扱う。
    private static decimal? ParsePricePer1k(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) && price > 0m
            ? price
            : null;
}

/// <summary>
/// 単価表の 1 行の構成値（文字列のまま。解析は <see cref="LlmPriceTable"/> が行う）。
/// 第 2 段（<paramref name="LongContextThresholdTokens"/> ほか）は任意で、入力トークン数が閾値を**超える**要求に効く（#1295, IADR-0524）。
/// </summary>
/// <param name="Model">モデル ID（`-` と `_` は同一視）。</param>
/// <param name="Input">第 1 段の入力単価（円/1k トークン）。</param>
/// <param name="Output">第 1 段の出力単価（円/1k トークン）。</param>
/// <param name="LongContextThresholdTokens">第 2 段に切り替わる入力トークン数の閾値（この値を超えると第 2 段）。</param>
/// <param name="LongContextInput">第 2 段の入力単価（円/1k トークン）。</param>
/// <param name="LongContextOutput">第 2 段の出力単価（円/1k トークン）。</param>
public readonly record struct LlmPriceRow(
    string Model,
    string? Input,
    string? Output,
    string? LongContextThresholdTokens = null,
    string? LongContextInput = null,
    string? LongContextOutput = null);

// 解析済みの 1 行（第 1 段と任意の第 2 段）。
internal sealed record LlmPriceEntry(LlmPrice Base, LlmLongContextPrice? LongContext);

// 第 2 段: 入力トークン数が ThresholdTokens を超える要求の単価。
internal sealed record LlmLongContextPrice(int ThresholdTokens, LlmPrice Price);
