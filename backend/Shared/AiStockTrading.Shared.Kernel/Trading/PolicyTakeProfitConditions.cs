using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Kernel.Trading;

/// <summary>方針の利確条件のしきい値の種類。</summary>
public enum TakeProfitThresholdKind
{
    /// <summary>平均取得単価からの含み益の率（%）。</summary>
    GainPercent,

    /// <summary>価格（<see cref="PolicyTakeProfitCondition.PriceCurrency"/> の通貨）。</summary>
    Price,
}

/// <summary>
/// 方針の「利確:」行から読んだ利確条件の 1 件。
/// </summary>
/// <param name="Symbol">対象の銘柄（大文字のティッカー）。null＝「全銘柄」。</param>
/// <param name="Kind">しきい値の種類。</param>
/// <param name="Threshold">しきい値（GainPercent は %、Price は価格）。常に正。</param>
/// <param name="PriceCurrency">価格の通貨（「$N」「N ドル」は USD、「N 円」は JPY）。GainPercent では null。</param>
/// <param name="PartialPercent">一部利確の割合（%。0 より大きく 100 以下）。書かれていなければ null。</param>
public sealed record PolicyTakeProfitCondition(
    string? Symbol,
    TakeProfitThresholdKind Kind,
    decimal Threshold,
    Currency? PriceCurrency,
    decimal? PartialPercent);

// FR-04, FR-07, ADR-0003, ADR-0048 決定 4, #1129, IADR-0470 決定 2・3（2026-10-01 追記 / #1129 再監査）:
// 方針の**決まった書式の「利確:」行だけ**から利確条件を読む（純関数・決定的）。
//
// 報告書サービス（方針を作る側。読める「利確:」行が無い方針を確定の前に警告する）と、取引判断サービス
// （方針を読む側。条件に達しているかをコードで比べ、達していればプロンプトで明示する）が**同じ部品**を引く
// ——警告が出ない方針なら判断側も条件を読める、という対応を 2 つの実装の食い違いで崩さないため（共有カーネルに置く）。
//
// 🔴 **自由文は読まない。** 2 度の独立監査で、自由文の読み取り（節ごとの銘柄・値幅や配分の語の除外 等）は
// 「達していないのに達した」と書く読み違いを出し続けた。書式の行だけを読めば、書式に合わない文から条件が生まれることは無い。
// 説明の文（人が読む方針）は従来どおり方針に残り、判断の LLM はそれも読む。ここが読むのは追加の「利確:」行だけである。
//
// 書式（1 行に 1 つ。NFKC で全角を半角へ寄せ、前後の空白を除いてから読む）:
//   [行頭の箇条書きの印 - * ・ のどれか]利確: <対象> <しきい値>[ (<割合>%)]
//   <対象>   大文字のティッカー [A-Z][A-Z0-9.]{0,9}／「全銘柄」／ティッカーのカンマ区切り（例 AAPL,MSFT。銘柄ごとの条件に展開する）
//   <しきい値> +N%（平均取得単価からの含み益の率。+ は必須）／$N・N ドル（USD の価格）／N 円（JPY の価格）。N は桁区切りのカンマ可
//   <割合>   括弧（全角可）で囲んだ N%（一部利確の割合。0 < N ≤ 100）。この 1 つの形だけを割合と読む
//   読んだ語の後ろに空白以外の文字があれば、その行は書式に合わない（「では利確しない」「以外」「割ったら」を条件にしない）。
//
// 🔴 **書式に合わない「利確:」行が 1 行でもあれば、方針全体を「読めない」とする**（条件 0 件＝警告が出て、判断には何も足さない）。
// 読めない行が特定の銘柄の上書きだった場合に、その行を飛ばして「全銘柄」や他の行を当てると、意図より低いしきい値で
// 「達した」と書きうるため（読めない行は「何も足さない」ではなく「全体を読まない」側へ倒す）。
//
// 銘柄への当て方: 銘柄を名指しした行があればその行だけ、無ければ「全銘柄」の行。同じ銘柄（または「全銘柄」）に
// 行が複数あれば、**すべてに達したときだけ**到達とする（しきい値の高いほうに合わせる＝最も控えめ）。
// 価格は利益の側だけを到達とし（ロングは価格 > 平均取得単価、ショートは価格 < 平均取得単価）、通貨が市場の通貨と違う価格は到達としない。
// 含み益の率が 0 以下なら、どの条件も到達としない（多重の防御）。
public static class PolicyTakeProfitConditions
{
    /// <summary>行の先頭の見出し（NFKC 後）。</summary>
    public const string LineLabel = "利確:";

    /// <summary>すべての銘柄に掛ける対象の語。</summary>
    public const string AllSymbolsTarget = "全銘柄";

    // 数値: 桁区切りのカンマ（3 桁ごと・最大 12 桁）または区切りなし（最大 12 桁）＋小数 4 桁まで。
    private const string Number = @"(?:\d{1,3}(?:,\d{3}){1,3}|\d{1,12})(?:\.\d{1,4})?";

    private const string Ticker = @"[A-Z][A-Z0-9.]{0,9}";

    // 「利確:」で始まる行（箇条書きの印は 1 つまで）。書式に合うかは FullLine で確かめる。
    private static readonly Regex LabelLine = new(@"\A(?:[-*・]\s*)?利確:", RegexOptions.CultureInvariant);

    private static readonly Regex FullLine = new(
        @"\A(?:[-*・]\s*)?利確:\s*"
        + $@"(?<target>{AllSymbolsTarget}|{Ticker}(?:\s*,\s*{Ticker})*)\s+"
        + $@"(?:\+(?<pct>{Number})\s*%|\$\s*(?<usd>{Number})|(?<usd2>{Number})\s*ドル|(?<jpy>{Number})\s*円)"
        + $@"(?:\s*\((?<part>{Number})\s*%\))?\z",
        RegexOptions.CultureInvariant);

    private static readonly Regex LineBreak = new(@"\r\n|\r|\n", RegexOptions.CultureInvariant);

    /// <summary>
    /// 方針の「利確:」行から利確条件を読む（出現順）。読める行が無い、または書式に合わない「利確:」行が 1 行でもあれば空。
    /// </summary>
    public static IReadOnlyList<PolicyTakeProfitCondition> Parse(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
            return [];

        // 全角の数字・％・＋・＄・：・（）・英字・空白を半角へ寄せる（NFKC）。
        var text = policy.Normalize(NormalizationForm.FormKC);
        var results = new List<PolicyTakeProfitCondition>();

        foreach (var raw in LineBreak.Split(text))
        {
            var line = raw.Trim();
            if (!LabelLine.IsMatch(line))
                continue;
            if (!TryParseLine(line, out var conditions))
                return [];
            results.AddRange(conditions);
        }

        return results;
    }

    /// <summary>読める「利確:」行が 1 行でもあり、書式に合わない「利確:」行が無いか。</summary>
    public static bool HasAny(string? policy) => Parse(policy).Count > 0;

    /// <summary>
    /// その銘柄に掛かる利確条件。銘柄を名指しした行があればその行だけを、無ければ「全銘柄」の行を返す
    /// （名指しのほうが具体的なため上書きする）。
    /// </summary>
    public static IReadOnlyList<PolicyTakeProfitCondition> ForSymbol(string? policy, string symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        var all = Parse(policy);
        var key = symbol.Trim().ToUpperInvariant();
        var named = all.Where(c => string.Equals(c.Symbol, key, StringComparison.Ordinal)).ToList();
        return named.Count > 0 ? named : all.Where(c => c.Symbol is null).ToList();
    }

    /// <summary>
    /// 条件の**すべて**に達していれば条件を（出現順）、1 つでも達していなければ空を返す（同じ銘柄の行が複数あるときは最も控えめに読む）。
    /// 含み益の率は (現在値 − 平均取得単価) ÷ 平均取得単価 × 100 をロングで、符号を反転してショートで計算する。
    /// 価格はロングで現在値 ≥ 価格、ショートで現在値 ≤ 価格で、価格が利益の側（ロングは価格 &gt; 平均取得単価、
    /// ショートは価格 &lt; 平均取得単価）にあり、通貨が <paramref name="marketCurrency"/> と同じものだけ。
    /// 条件が空・取得単価や現在値が正でない・含み益の率が 0 以下なら空（推測しない・含み損の建玉を「達した」と書かない）。
    /// </summary>
    public static IReadOnlyList<PolicyTakeProfitCondition> Reached(
        IReadOnlyList<PolicyTakeProfitCondition> conditions, bool isLong, decimal averageEntryPrice, decimal markPrice,
        Currency marketCurrency)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        if (conditions.Count == 0 || averageEntryPrice <= 0m || markPrice <= 0m)
            return [];

        var gain = GainPercent(isLong, averageEntryPrice, markPrice);
        // FR-04, #1129（監査 F2）: 多重の防御。含み益が無い建玉に「利確条件に達した」とは書かない。
        if (gain <= 0m)
            return [];

        var all = conditions.All(c => c.Kind switch
        {
            TakeProfitThresholdKind.GainPercent => gain >= c.Threshold,
            // FR-04, #1129（監査 F2）: 価格は利益の側にあるものだけを利確の水準と読む（ショートで取得単価より上の価格は損の側）。
            TakeProfitThresholdKind.Price => c.PriceCurrency == marketCurrency && (isLong
                ? c.Threshold > averageEntryPrice && markPrice >= c.Threshold
                : c.Threshold < averageEntryPrice && markPrice <= c.Threshold),
            _ => false,
        });
        return all ? conditions : [];
    }

    /// <summary>平均取得単価からの含み益の率（%）。ショートは値下がりが正。</summary>
    public static decimal GainPercent(bool isLong, decimal averageEntryPrice, decimal markPrice)
    {
        if (averageEntryPrice <= 0m)
            throw new ArgumentOutOfRangeException(nameof(averageEntryPrice), "平均取得単価は正でなければなりません。");
        var rate = (markPrice - averageEntryPrice) / averageEntryPrice * 100m;
        return isLong ? rate : -rate;
    }

    /// <summary>条件の表示（例「取得単価から +5% で利確（一部利確 50%）」「価格 230 USD で利確」）。</summary>
    public static string Describe(PolicyTakeProfitCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var ci = CultureInfo.InvariantCulture;
        var head = condition.Kind == TakeProfitThresholdKind.GainPercent
            ? $"取得単価から +{condition.Threshold.ToString("0.####", ci)}% で利確"
            : $"価格 {condition.Threshold.ToString("0.####", ci)}"
              + (condition.PriceCurrency is { } currency ? $" {CurrencyFormat.CodeOf(currency)}" : string.Empty)
              + " で利確";
        return condition.PartialPercent is { } partial
            ? $"{head}（一部利確 {partial.ToString("0.####", ci)}%）"
            : head;
    }

    private static bool TryParseLine(string line, out List<PolicyTakeProfitCondition> conditions)
    {
        conditions = [];
        var m = FullLine.Match(line);
        if (!m.Success)
            return false;

        TakeProfitThresholdKind kind;
        Currency? currency;
        string raw;
        if (m.Groups["pct"].Success)
            (kind, currency, raw) = (TakeProfitThresholdKind.GainPercent, null, m.Groups["pct"].Value);
        else if (m.Groups["usd"].Success)
            (kind, currency, raw) = (TakeProfitThresholdKind.Price, Currency.Usd, m.Groups["usd"].Value);
        else if (m.Groups["usd2"].Success)
            (kind, currency, raw) = (TakeProfitThresholdKind.Price, Currency.Usd, m.Groups["usd2"].Value);
        else
            (kind, currency, raw) = (TakeProfitThresholdKind.Price, Currency.Jpy, m.Groups["jpy"].Value);

        if (!TryNumber(raw, out var threshold) || threshold <= 0m)
            return false;

        decimal? partial = null;
        if (m.Groups["part"].Success)
        {
            if (!TryNumber(m.Groups["part"].Value, out var p) || p <= 0m || p > 100m)
                return false;
            partial = p;
        }

        var target = m.Groups["target"].Value;
        if (target == AllSymbolsTarget)
        {
            conditions.Add(new PolicyTakeProfitCondition(null, kind, threshold, currency, partial));
            return true;
        }

        foreach (var symbol in target.Split(',').Select(s => s.Trim()))
            conditions.Add(new PolicyTakeProfitCondition(symbol, kind, threshold, currency, partial));
        return true;
    }

    // 桁区切りのカンマを除いてから読む（書式の正規表現が 3 桁ごとの区切りだけを通している）。
    private static bool TryNumber(string raw, out decimal value) =>
        decimal.TryParse(
            raw.Replace(",", string.Empty, StringComparison.Ordinal),
            NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
}
