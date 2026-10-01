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
/// <param name="Symbols">条件の節が名指しした銘柄（大文字のティッカー）。空＝銘柄を名指ししない（<paramref name="SubjectUnresolved"/> が false ならどの銘柄にも掛かる）。</param>
/// <param name="Kind">しきい値の種類。</param>
/// <param name="Threshold">しきい値（GainPercent は %、Price は価格）。常に正。</param>
/// <param name="PartialPercent">一部利確の割合（%）。書かれていなければ null。</param>
/// <param name="SubjectUnresolved">
/// 条件がどの銘柄のものか決められない（節に銘柄が無く文に銘柄が 2 つ以上ある・ティッカーでない社名やコードを名指しした）。
/// 数値の利確条件としては数える（<see cref="PolicyTakeProfitConditions.HasAny"/>。方針は数値を書いている）が、
/// どの銘柄にも掛けない（<see cref="PolicyTakeProfitConditions.ForSymbol"/>。取り違えて「達した」と書くより安全側）。
/// </param>
public sealed record PolicyTakeProfitCondition(
    IReadOnlyList<string> Symbols,
    TakeProfitThresholdKind Kind,
    decimal Threshold,
    decimal? PartialPercent,
    bool SubjectUnresolved = false);

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
//   2. 文を「、」「,」で節に分け、次の語を含む節の数値は使わない（売りの条件でも利確ではない／基準・対象が違う）:
//      損切り・損失・下落・含み損 等／取得単価以外を基準にする語（前日・始値・高値・安値・出来高・指数・日経平均・S&P・ダウ・ナスダック 等。
//      「平均取得単価」は除く）／配分・建てる量の語（口座・資金・1 銘柄あたり・上限・配分・比率 等）／時間の語（期間・経過 等）／
//      利確でない売買の語（買い増し・新規・エントリー 等）。
//   3. 数値を読む節は、利確の語を含む節と、そこから途切れずに続く（前後に隣り合う）数値を持つ節だけ
//      （「+3% で半分、+6% で残りを利確」の段階の利確は読み、数値の無い節を挟んだ先の % は利確に結び付けない）。
//   4. 残った節の「N%」は、直後が「を・分を・だけ・ずつ」か、直前が「保有の・数量の・株数の・建玉の」なら一部利確の割合。
//      節に「N%」が 2 つ以上あり、直後が「（を）利確・売却・決済」のものも一部利確の割合（「+5% で 50% 利確」）。
//      それ以外で負号の無いものは含み益の率のしきい値。「半分・半数」は 50%、「N割」は N×10% の一部利確。
//      「$N」「N ドル」「N USD」「N 円」は価格のしきい値。ただし符号つき（「+5 ドル」）・直後が「高・安・分・幅」・
//      節に値幅や利益の額の語（上昇・上がったら・下がったら・幅・利益が・含み益 等）があれば、値幅・金額であって価格ではないので読まない。
//   5. 銘柄は節ごとに決める。節の大文字のティッカー（一般語の略語は除く）がその節の条件の銘柄。節に無ければ、文の銘柄が
//      ちょうど 1 つならそれ、2 つ以上なら決められない（SubjectUnresolved）。ティッカーでない社名（カタカナ・漢字・英字の名前＋「は」「:」）や
//      4 桁のコードを名指しした文も決められない。決められない条件は数値の利確条件として数えるが、どの銘柄にも掛けない。
//   6. 価格の条件は利益の側だけを到達とする（ロングは価格 > 平均取得単価、ショートは価格 < 平均取得単価）。
//      含み益の率が 0 以下なら、どの条件も到達としない（多重の防御）。
public static class PolicyTakeProfitConditions
{
    /// <summary>1 つの方針から取り出す条件の上限（プロンプトの行が際限なく伸びない）。</summary>
    public const int MaxConditions = 20;

    private static readonly Regex SentenceSplit = new(@"[\n。；;]", RegexOptions.CultureInvariant);

    private static readonly Regex ClauseSplit = new(@"[、,，]", RegexOptions.CultureInvariant);

    private static readonly Regex TakeProfitWord = new(@"利確|利益確定|利食い|利益を確定", RegexOptions.CultureInvariant);

    private static readonly Regex LossWord =
        new(@"損切|損失|下落|含み損|逆指値|ストップ|マイナス", RegexOptions.CultureInvariant);

    // 取得単価以外を基準にする語（指数の名前を含む。「平均取得単価」「取得平均」「平均単価」の平均は除く）・利確のしきい値ではない金額の語。
    private static readonly Regex OtherBasisWord = new(
        @"前日|前営業日|始値|高値|安値|出来高|指数|(?<!取得)平均(?!取得|単価)|日経|S&P|ダウ|ナスダック|NASDAQ|TOPIX|RSI|金額|予算|損益目標|目標損益",
        RegexOptions.CultureInvariant);

    // 配分・建てる量の語（「1 銘柄あたり口座の 10% まで」の 10% は利確のしきい値ではない）。
    private static readonly Regex AllocationWord =
        new(@"口座|資金|銘柄あたり|上限|配分|比率|投入|組入|余力|ウェイト|ウエイト", RegexOptions.CultureInvariant);

    // 時間の語（「期間の 50% が経過」の 50% は含み益の率ではない）。
    private static readonly Regex TimeWord = new(@"期間|経過|日以内|日後|週間|か月|ヶ月", RegexOptions.CultureInvariant);

    // 利確でない売買の語（「+3% で買い増し」の +3% は利確のしきい値ではない）。
    private static readonly Regex OtherActionWord =
        new(@"買い増|売り増|ナンピン|新規|エントリー|買い付け|買う", RegexOptions.CultureInvariant);

    // 値幅・利益の額の語。この語のある節の金額は価格の水準ではない（「5 ドル上昇したら」「利益が 500 ドルに達したら」）。
    // 「利益確定」「利益を確定」の利益は利確の語なので除く。
    private static readonly Regex WidthWord =
        new(@"上昇|上が|値上|下が|値下|幅|利益(?!確定|を確定)|含み益|儲け", RegexOptions.CultureInvariant);

    // 金額の直後が値幅を表す語（「5 ドル高」「5 ドル分」）。
    private static readonly Regex WidthAfter = new(@"\A\s*(?:高|安|分|幅)", RegexOptions.CultureInvariant);

    // 「N%」（符号つき可）。数値は 1〜6 桁＋小数。
    private static readonly Regex PercentToken =
        new(@"(?<sign>[+\-−])?\s*(?<num>\d{1,6}(?:\.\d+)?)\s*%", RegexOptions.CultureInvariant);

    // 一部利確の割合と読む「N%」の後ろ（を・分を・だけ・ずつ）。
    private static readonly Regex PartialAfter = new(@"\A\s*(?:分)?\s*(?:を|だけ|ずつ)", RegexOptions.CultureInvariant);

    // 節に「N%」が 2 つ以上あるとき、一部利確の割合と読む「N%」の後ろ（（を）利確・売却・決済）。「+5% で 50% 利確」。
    private static readonly Regex PartialActionAfter =
        new(@"\A\s*(?:を\s*)?(?:利確|利益確定|利食い|売却|決済|手仕舞)", RegexOptions.CultureInvariant);

    // 一部利確の割合と読む「N%」の前（保有の・数量の 等）。
    private static readonly Regex PartialBefore =
        new(@"(?:保有|数量|株数|建玉|ポジション)(?:の)?\s*\z", RegexOptions.CultureInvariant);

    private static readonly Regex HalfWord = new(@"半分|半数", RegexOptions.CultureInvariant);

    private static readonly Regex TenthsToken = new(@"(?<num>\d{1,2})\s*割", RegexOptions.CultureInvariant);

    private static readonly Regex PriceToken = new(
        @"(?<sign>[+\-−])?\s*(?:\$\s*(?<num1>\d{1,7}(?:\.\d+)?)|(?<num2>\d{1,7}(?:\.\d+)?)\s*(?:ドル|USD|円))",
        RegexOptions.CultureInvariant);

    // ティッカーでない名指しの主語（カタカナ・漢字・英字の名前＋「は」「:」、4 桁のコード）。「アップルは」「トヨタ(7203)は」「任天堂:」。
    private static readonly Regex NameSubject = new(
        @"(?<name>(?<![\p{IsKatakana}ー・])[\p{IsKatakana}ー・]{2,}|(?<![\p{IsCJKUnifiedIdeographs}々])[\p{IsCJKUnifiedIdeographs}々]{2,}|(?<![A-Za-z])[A-Z][a-z]+)\s*(?:は|:)",
        RegexOptions.CultureInvariant);

    private static readonly Regex StockCode =
        new(@"\(\s*\d{4}\s*\)|(?<![\d.])\d{4}(?![\d.%])\s*(?:は|:)", RegexOptions.CultureInvariant);

    // 名前の形をした一般語（主語として銘柄を名指ししない）。末尾が「関連・銘柄・株」等の語も一般語。
    private static readonly HashSet<string> GenericSubjects = new(StringComparer.Ordinal)
    {
        "利確", "利益確定", "利食", "全銘柄", "各銘柄", "保有", "保有中", "保有株", "方針", "原則", "基本", "目標", "条件", "本日",
        "今日", "当日", "明日", "全体", "全部", "個別", "既存", "残数", "残高", "半分", "半数", "株価", "価格", "現在値", "終値",
        "上昇", "利益", "売買", "売り", "取引", "対象", "以後", "以降", "通常", "ロング", "ショート", "ポジション", "ルール",
        "トレード", "スイング", "プラス",
    };

    private static readonly Regex GenericSubjectSuffix = new(@"(?:関連|銘柄|株|セクター|業種|全体|系)\z", RegexOptions.CultureInvariant);

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

            var clauses = ClauseSplit.Split(sentence);
            var clauseTickers = clauses.Select(Tickers).ToArray();
            var sentenceTickers = clauseTickers.SelectMany(t => t).Distinct(StringComparer.Ordinal).ToList();
            // ティッカーの無い節が社名・コードを名指ししていれば、その文の銘柄の無い節の主語は決められない。
            var namesOther = clauses.Where((c, i) => clauseTickers[i].Count == 0 && NamesNonTicker(c)).Any();
            var linked = LinkedClauses(clauses);

            for (var i = 0; i < clauses.Length; i++)
            {
                if (!linked[i])
                    continue;

                // FR-04, #1129（監査 F1・F5）: 銘柄は節ごとに決める。決められなければどの銘柄にも掛けない。
                var (symbols, unresolved) = clauseTickers[i].Count > 0
                    ? (clauseTickers[i], false)
                    : namesOther || sentenceTickers.Count >= 2
                        ? ([], true)
                        : (sentenceTickers, false);

                foreach (var condition in ParseClause(clauses[i], symbols, unresolved))
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
        return named.Count > 0 ? named : all.Where(c => c.Symbols.Count == 0 && !c.SubjectUnresolved).ToList();
    }

    /// <summary>
    /// 達している条件（出現順）。含み益の率は (現在値 − 平均取得単価) ÷ 平均取得単価 × 100 をロングで、
    /// 符号を反転してショートで計算する。価格はロングで現在値 ≥ 価格、ショートで現在値 ≤ 価格で、価格が利益の側
    /// （ロングは価格 &gt; 平均取得単価、ショートは価格 &lt; 平均取得単価）にあるものだけ。
    /// 取得単価・現在値が正でない、または含み益の率が 0 以下なら何も返さない（推測しない・含み損の建玉を「達した」と書かない）。
    /// </summary>
    public static IReadOnlyList<PolicyTakeProfitCondition> Reached(
        IReadOnlyList<PolicyTakeProfitCondition> conditions, bool isLong, decimal averageEntryPrice, decimal markPrice)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        if (averageEntryPrice <= 0m || markPrice <= 0m)
            return [];

        var gain = GainPercent(isLong, averageEntryPrice, markPrice);
        // FR-04, #1129（監査 F2）: 多重の防御。含み益が無い建玉に「利確条件に達した」とは書かない。
        if (gain <= 0m)
            return [];

        return conditions.Where(c => c.Kind switch
        {
            TakeProfitThresholdKind.GainPercent => gain >= c.Threshold,
            // FR-04, #1129（監査 F2）: 価格は利益の側にあるものだけを利確の水準と読む（ショートで取得単価より上の価格は損の側）。
            TakeProfitThresholdKind.Price => isLong
                ? c.Threshold > averageEntryPrice && markPrice >= c.Threshold
                : c.Threshold < averageEntryPrice && markPrice <= c.Threshold,
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

    private static List<string> Tickers(string clause) =>
        TickerToken.Matches(clause)
            .Select(m => m.Value)
            .Where(s => !NonTickerWords.Contains(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static bool NamesNonTicker(string clause) =>
        StockCode.IsMatch(clause)
        || NameSubject.Matches(clause).Any(m =>
        {
            var name = m.Groups["name"].Value;
            return !GenericSubjects.Contains(name) && !GenericSubjectSuffix.IsMatch(name);
        });

    // FR-04, #1129（監査 F4）: 数値を読む節。除外の語の無い、利確の語を含む節と、そこから途切れずに隣り合う数値を持つ節。
    private static bool[] LinkedClauses(string[] clauses)
    {
        var usable = clauses.Select(c => !LossWord.IsMatch(c) && !OtherBasisWord.IsMatch(c) && !AllocationWord.IsMatch(c)
            && !TimeWord.IsMatch(c) && !OtherActionWord.IsMatch(c)).ToArray();
        var hasNumber = clauses.Select(c => PercentToken.IsMatch(c) || PriceToken.IsMatch(c)).ToArray();
        var linked = new bool[clauses.Length];
        for (var i = 0; i < clauses.Length; i++)
        {
            if (!usable[i] || !TakeProfitWord.IsMatch(clauses[i]))
                continue;
            linked[i] = true;
            for (var j = i - 1; j >= 0 && usable[j] && hasNumber[j]; j--)
                linked[j] = true;
            for (var j = i + 1; j < clauses.Length && usable[j] && hasNumber[j]; j++)
                linked[j] = true;
        }

        return linked;
    }

    private static IEnumerable<PolicyTakeProfitCondition> ParseClause(
        string clause, IReadOnlyList<string> symbols, bool subjectUnresolved)
    {
        decimal? partial = null;
        var thresholds = new List<(TakeProfitThresholdKind Kind, decimal Value)>();
        var percents = PercentToken.Matches(clause);

        foreach (Match m in percents)
        {
            if (!TryNumber(m.Groups["num"].Value, out var value) || value <= 0m)
                continue;

            var after = clause[(m.Index + m.Length)..];
            var before = clause[..m.Index];
            // FR-04, #1129（監査 F3）: 「+5% で 50% 利確」の 50% は一部利確の割合（しきい値ではない）。
            var partialByAction = percents.Count >= 2 && !m.Groups["sign"].Success && PartialActionAfter.IsMatch(after);
            if (PartialAfter.IsMatch(after) || PartialBefore.IsMatch(before) || partialByAction)
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

        // FR-04, #1129（監査 F2）: 値幅・利益の額は価格の水準ではない（「5 ドル上昇したら」「利益が 500 ドルに達したら」「+5 ドル」）。
        if (!WidthWord.IsMatch(clause))
        {
            foreach (Match m in PriceToken.Matches(clause))
            {
                if (m.Groups["sign"].Success || WidthAfter.IsMatch(clause[(m.Index + m.Length)..]))
                    continue;
                var raw = m.Groups["num1"].Success ? m.Groups["num1"].Value : m.Groups["num2"].Value;
                if (TryNumber(raw, out var price) && price > 0m)
                    thresholds.Add((TakeProfitThresholdKind.Price, price));
            }
        }

        foreach (var (kind, value) in thresholds)
            yield return new PolicyTakeProfitCondition(symbols, kind, value, partial, subjectUnresolved);
    }

    private static bool TryNumber(string raw, out decimal value) =>
        decimal.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
}
