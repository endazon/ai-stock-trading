using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AiStockTrading.Shared.Kernel.Trading;

/// <summary>方針の利確条件のしきい値の種類。</summary>
public enum TakeProfitThresholdKind
{
    /// <summary>平均取得単価からの含み益の率（%）。</summary>
    GainPercent,

    /// <summary>価格（銘柄の市場の通貨）。</summary>
    Price,
}

/// <summary>
/// 方針の文から決定的に取り出した利確条件の 1 件。
/// </summary>
/// <param name="Symbols">条件の文が名指しした銘柄（大文字のティッカー）。空＝銘柄を名指ししない（どの銘柄にも掛かる）。</param>
/// <param name="Kind">しきい値の種類。</param>
/// <param name="Threshold">しきい値（GainPercent は %、Price は価格）。常に正。</param>
/// <param name="PartialPercent">一部利確の割合（%）。書かれていなければ null。</param>
public sealed record PolicyTakeProfitCondition(
    IReadOnlyList<string> Symbols,
    TakeProfitThresholdKind Kind,
    decimal Threshold,
    decimal? PartialPercent);

// FR-04, FR-07, ADR-0003, ADR-0048 決定 4, #1129, IADR-0470 決定 2・3: 方針の文から**数値の利確条件**を取り出す（純関数・決定的）。
//
// 報告書サービス（方針を作る側。数値の利確条件が 1 件も無い方針を確定の前に警告する）と、取引判断サービス
// （方針を読む側。条件に達しているかをコードで比べ、達していればプロンプトで明示する）が**同じ部品**を引く
// ——警告が出ない方針なら判断側も条件を読める、という対応を 2 つの実装の食い違いで崩さないため（共有カーネルに置く）。
//
// 🔴 **取り出せないときは何も返さない（推測しない）。** 呼び出し側は「条件なし」として従来どおりに振る舞う
// （判断側は何も明示しない・既定の Hold の規則は変えない）。取り違えて「達した」と書くより、書かないほうが安全側である。
//
// 読み方（1 文＝改行・「。」・「；」で区切った単位）:
//   1. 利確の語（利確・利益確定・利食い）を含む文だけを見る。
//   2. 文を「、」「,」で節に分け、損切り・損失・下落・含み損の語を含む節と、取得単価以外を基準にする語
//      （前日・始値・高値・安値・出来高・指数）・しきい値でない金額の語（上限・資金 等）を含む節の数値は使わない（売りの条件でも利確ではない／基準が違う）。
//   3. 残った節の「N%」は、直後が「を・分を・だけ・ずつ」か、直前が「保有の・数量の・株数の・建玉の」なら一部利確の割合、
//      それ以外で負号の無いものは含み益の率のしきい値。「半分・半数」は 50%、「N割」は N×10% の一部利確。
//      「$N」「N ドル」「N USD」「N 円」は価格のしきい値。
//   4. 文の中の大文字のティッカー（一般語の略語は除く）が、その文の条件の銘柄になる。
public static class PolicyTakeProfitConditions
{
    /// <summary>1 つの方針から取り出す条件の上限（プロンプトの行が際限なく伸びない）。</summary>
    public const int MaxConditions = 20;

    private static readonly Regex SentenceSplit = new(@"[\n。；;]", RegexOptions.CultureInvariant);

    private static readonly Regex ClauseSplit = new(@"[、,，]", RegexOptions.CultureInvariant);

    private static readonly Regex TakeProfitWord = new(@"利確|利益確定|利食い|利益を確定", RegexOptions.CultureInvariant);

    private static readonly Regex LossWord =
        new(@"損切|損失|下落|含み損|逆指値|ストップ|マイナス", RegexOptions.CultureInvariant);

    // 取得単価以外を基準にする語・利確のしきい値ではない金額の語（上限・資金など）。
    private static readonly Regex OtherBasisWord =
        new(@"前日|前営業日|始値|高値|安値|出来高|指数|移動平均|RSI|上限|金額|資金|予算|損益目標|目標損益", RegexOptions.CultureInvariant);

    // 「N%」（符号つき可）。数値は 1〜6 桁＋小数。
    private static readonly Regex PercentToken =
        new(@"(?<sign>[+\-−])?\s*(?<num>\d{1,6}(?:\.\d+)?)\s*%", RegexOptions.CultureInvariant);

    // 一部利確の割合と読む「N%」の後ろ（を・分を・だけ・ずつ）。
    private static readonly Regex PartialAfter = new(@"\A\s*(?:分)?\s*(?:を|だけ|ずつ)", RegexOptions.CultureInvariant);

    // 一部利確の割合と読む「N%」の前（保有の・数量の 等）。
    private static readonly Regex PartialBefore =
        new(@"(?:保有|数量|株数|建玉|ポジション)(?:の)?\s*\z", RegexOptions.CultureInvariant);

    private static readonly Regex HalfWord = new(@"半分|半数", RegexOptions.CultureInvariant);

    private static readonly Regex TenthsToken = new(@"(?<num>\d{1,2})\s*割", RegexOptions.CultureInvariant);

    private static readonly Regex PriceToken = new(
        @"\$\s*(?<num1>\d{1,7}(?:\.\d+)?)|(?<num2>\d{1,7}(?:\.\d+)?)\s*(?:ドル|USD|円)",
        RegexOptions.CultureInvariant);

    // 米国のティッカーの形（大文字 1〜5 文字＋任意の区切りと 1〜2 文字）。英数字に挟まれたものは拾わない。
    private static readonly Regex TickerToken =
        new(@"(?<![A-Za-z0-9])[A-Z]{1,5}(?:[.\-][A-Z]{1,2})?(?![A-Za-z0-9])", RegexOptions.CultureInvariant);

    // ティッカーの形をした一般語（銘柄として読まない）。漏れは「その文を特定の銘柄の条件と読む」側へ倒れ、
    // 判断側では他の銘柄に掛からなくなる（＝何も明示しない側。安全側）。
    private static readonly HashSet<string> NonTickerWords = new(StringComparer.Ordinal)
    {
        "USD", "JPY", "ETF", "AI", "RSI", "VWAP", "PER", "PBR", "EPS", "ROE", "IPO", "SEC", "TAF", "JST", "ET",
        "EST", "EDT", "UTC", "FOMC", "CPI", "PPI", "GDP", "PCE", "FRB", "FED", "NYSE", "LLM", "OK", "NG", "PNL",
        "TP", "SL", "ATR", "MA", "SMA", "EMA", "MACD", "YAML", "Q", "A", "B", "S", "P",
    };

    /// <summary>方針の文から利確条件を取り出す（最大 <see cref="MaxConditions"/> 件・出現順）。</summary>
    public static IReadOnlyList<PolicyTakeProfitCondition> Extract(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
            return [];

        // 全角の数字・％・＋・＄・英字を半角へ寄せる（NFKC）。
        var text = policy.Normalize(NormalizationForm.FormKC);
        var results = new List<PolicyTakeProfitCondition>();

        foreach (var sentence in SentenceSplit.Split(text))
        {
            if (!TakeProfitWord.IsMatch(sentence))
                continue;

            var symbols = TickerToken.Matches(sentence)
                .Select(m => m.Value)
                .Where(s => !NonTickerWords.Contains(s))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (var clause in ClauseSplit.Split(sentence))
            {
                if (LossWord.IsMatch(clause) || OtherBasisWord.IsMatch(clause))
                    continue;

                foreach (var condition in ParseClause(clause, symbols))
                {
                    results.Add(condition);
                    if (results.Count >= MaxConditions)
                        return results;
                }
            }
        }

        return results;
    }

    /// <summary>数値の利確条件が 1 件でも取り出せるか。</summary>
    public static bool HasAny(string? policy) => Extract(policy).Count > 0;

    /// <summary>
    /// その銘柄に掛かる利確条件。銘柄を名指しした条件があればそれだけを、無ければ銘柄を名指ししない条件を返す
    /// （名指しのほうが具体的なため優先する）。
    /// </summary>
    public static IReadOnlyList<PolicyTakeProfitCondition> ForSymbol(string? policy, string symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        var all = Extract(policy);
        var key = symbol.Trim().ToUpperInvariant();
        var named = all.Where(c => c.Symbols.Contains(key, StringComparer.Ordinal)).ToList();
        return named.Count > 0 ? named : all.Where(c => c.Symbols.Count == 0).ToList();
    }

    /// <summary>
    /// 達している条件（出現順）。含み益の率は (現在値 − 平均取得単価) ÷ 平均取得単価 × 100 をロングで、
    /// 符号を反転してショートで計算する。価格はロングで現在値 ≥ 価格、ショートで現在値 ≤ 価格。
    /// 取得単価・現在値が正でなければ何も返さない（推測しない）。
    /// </summary>
    public static IReadOnlyList<PolicyTakeProfitCondition> Reached(
        IReadOnlyList<PolicyTakeProfitCondition> conditions, bool isLong, decimal averageEntryPrice, decimal markPrice)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        if (averageEntryPrice <= 0m || markPrice <= 0m)
            return [];

        var gain = GainPercent(isLong, averageEntryPrice, markPrice);
        return conditions.Where(c => c.Kind switch
        {
            TakeProfitThresholdKind.GainPercent => gain >= c.Threshold,
            TakeProfitThresholdKind.Price => isLong ? markPrice >= c.Threshold : markPrice <= c.Threshold,
            _ => false,
        }).ToList();
    }

    /// <summary>平均取得単価からの含み益の率（%）。ショートは値下がりが正。</summary>
    public static decimal GainPercent(bool isLong, decimal averageEntryPrice, decimal markPrice)
    {
        if (averageEntryPrice <= 0m)
            throw new ArgumentOutOfRangeException(nameof(averageEntryPrice), "平均取得単価は正でなければなりません。");
        var rate = (markPrice - averageEntryPrice) / averageEntryPrice * 100m;
        return isLong ? rate : -rate;
    }

    /// <summary>条件の表示（例「取得単価から +5% で利確（一部利確 50%）」「価格 230 で利確」）。</summary>
    public static string Describe(PolicyTakeProfitCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var ci = CultureInfo.InvariantCulture;
        var head = condition.Kind == TakeProfitThresholdKind.GainPercent
            ? $"取得単価から +{condition.Threshold.ToString("0.####", ci)}% で利確"
            : $"価格 {condition.Threshold.ToString("0.####", ci)} で利確";
        return condition.PartialPercent is { } partial
            ? $"{head}（一部利確 {partial.ToString("0.####", ci)}%）"
            : head;
    }

    private static IEnumerable<PolicyTakeProfitCondition> ParseClause(string clause, IReadOnlyList<string> symbols)
    {
        decimal? partial = null;
        var thresholds = new List<(TakeProfitThresholdKind Kind, decimal Value)>();

        foreach (Match m in PercentToken.Matches(clause))
        {
            if (!TryNumber(m.Groups["num"].Value, out var value) || value <= 0m)
                continue;

            var after = clause[(m.Index + m.Length)..];
            var before = clause[..m.Index];
            if (PartialAfter.IsMatch(after) || PartialBefore.IsMatch(before))
            {
                if (value <= 100m)
                    partial ??= value;
                continue;
            }

            var sign = m.Groups["sign"].Value;
            if (sign is "-" or "−")
                continue;
            if (value <= 1000m)
                thresholds.Add((TakeProfitThresholdKind.GainPercent, value));
        }

        if (partial is null && HalfWord.IsMatch(clause))
            partial = 50m;

        if (partial is null && TenthsToken.Match(clause) is { Success: true } tenths
            && TryNumber(tenths.Groups["num"].Value, out var t) && t is > 0m and <= 10m)
        {
            partial = t * 10m;
        }

        foreach (Match m in PriceToken.Matches(clause))
        {
            var raw = m.Groups["num1"].Success ? m.Groups["num1"].Value : m.Groups["num2"].Value;
            if (TryNumber(raw, out var price) && price > 0m)
                thresholds.Add((TakeProfitThresholdKind.Price, price));
        }

        foreach (var (kind, value) in thresholds)
            yield return new PolicyTakeProfitCondition(symbols, kind, value, partial);
    }

    private static bool TryNumber(string raw, out decimal value) =>
        decimal.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
}
