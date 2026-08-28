namespace TradeDecisionService.Domain;

// FR-02, FR-04, ADR-0003, #337, IADR-0247: スクリーニング入力のコンテキスト超過時の縮退計画（純関数）。
//
// 計画 06_technical/01_architecture-overview「判断の二段化」（利用者裁定 2026-08-02・planning#53）の縮退手順を写像する。
//
//   | 順 | 動作 | 判断材料への影響 |
//   | 1 | スクリーニング対象の銘柄を分割し、複数回に分けて呼び出す | なし（銘柄ごとに独立した評価のため） |
//   | 2 | 分割しても 1 回分が収まらない場合に限り、RAG で引いた過去の判断・振り返りを削る | あり |
//   | 3 | それでも収まらない場合、収集したニュース・開示情報を古い順・関連度の低い順に削る | あり |
//
// 🔴 **削ってはならないもの（確定した日報の方針・当日の市況・価格データ）は、削減可能集合に
// 入らない＝型として削れない**（CollectionDegradation の ClosesAllowed と同じ構造防御）。本型は
// それらを「サイズ（保護分）」としてしか受け取らず、全段を使っても収まらない場合は
// UnresolvableOverflow を立てて**削らずに**返す。
//
// 🔴 **上位モデルへの退避は採らない**（利用者裁定 2026-08-02。退避すると二段化の費用統制がその場で
// 無効になる）。本型はモデルという概念を持たない——退避の選択肢が構造的に存在しない。
//
// **分割（材料は減らない）と切り詰め（材料が減る）は分けて数える**（月報の件数記載の単位）。
public static class ScreeningContextPlanner
{
    public static ScreeningContextPlan Plan(
        int sharedProtectedChars,
        IReadOnlyList<ScreeningSymbolLoad> symbols,
        IReadOnlyList<ScreeningMaterial> materials,
        int budgetChars)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentOutOfRangeException.ThrowIfNegative(sharedProtectedChars);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetChars);

        if (symbols.Count == 0)
        {
            return new ScreeningContextPlan([], false, 0, 0, false);
        }

        var materialTotal = materials.Sum(m => m.Chars);

        // 段 1: 分割（材料は減らさない）。銘柄を First-Fit Decreasing で詰め、予算内に収まる呼び出し単位を作る。
        // 全銘柄が 1 バッチで収まれば分割なし。単独でも収まらない銘柄が出たら段 2 へ。
        var packed = TryPack(sharedProtectedChars, symbols, materialTotal, budgetChars);
        if (packed is not null)
        {
            return new ScreeningContextPlan(
                packed.Select(b => new ScreeningBatch(b, materials, ExceedsBudget: false)).ToList(),
                SplitOccurred: packed.Count > 1,
                DroppedRagCount: 0,
                DroppedNewsCount: 0,
                UnresolvableOverflow: false);
        }

        // 段 2・段 3: 最大分割（1 銘柄 = 1 呼び出し）でも収まらない。材料を規定の順で削る。
        // 最も重い銘柄（保護分が最大）が収まるまで削れば、すべての銘柄が収まる。
        var worstProtected = sharedProtectedChars + symbols.Max(s => s.ProtectedChars);
        var retained = materials.ToList();
        var droppedRag = 0;
        var droppedNews = 0;

        // 段 2: RAG（過去の判断・振り返り）を関連度の低い順に削る（補助的な情報であり、無くても判断は成立する）。
        foreach (var rag in materials
                     .Where(m => m.Kind == ScreeningMaterialKind.RagReference)
                     .OrderBy(m => m.Relevance)
                     .ThenBy(m => m.Index))
        {
            if (worstProtected + retained.Sum(m => m.Chars) <= budgetChars)
                break;

            retained.Remove(rag);
            droppedRag++;
        }

        // 段 3: ニュース・開示を**古い順・関連度の低い順**に削る。発行時刻が不明（null）なものは
        // 最古として先に削る（保守側——新しさを主張できない材料から手放す）。
        foreach (var news in materials
                     .Where(m => m.Kind == ScreeningMaterialKind.NewsDisclosure)
                     .OrderBy(m => m.PublishedAt.HasValue ? 1 : 0)
                     .ThenBy(m => m.PublishedAt ?? DateTimeOffset.MinValue)
                     .ThenBy(m => m.Relevance)
                     .ThenBy(m => m.Index))
        {
            if (worstProtected + retained.Sum(m => m.Chars) <= budgetChars)
                break;

            retained.Remove(news);
            droppedNews++;
        }

        // 削り切っても収まらない ＝ 保護対象（方針・市況）だけで予算超過。**保護対象は削らない**。
        // 呼び出しは行い、超過の事実を記録する（上位モデルへの退避もしない）。
        var stillOver = worstProtected + retained.Sum(m => m.Chars) > budgetChars;

        var batches = symbols
            .Select(s => new ScreeningBatch(
                [s], retained,
                ExceedsBudget: sharedProtectedChars + s.ProtectedChars + retained.Sum(m => m.Chars) > budgetChars))
            .ToList();

        return new ScreeningContextPlan(
            batches,
            SplitOccurred: symbols.Count > 1,
            DroppedRagCount: droppedRag,
            DroppedNewsCount: droppedNews,
            UnresolvableOverflow: stillOver);
    }

    // 段 1 の詰め。すべての銘柄が予算内のバッチへ入れば列を返す。単独でも収まらない銘柄が
    // あれば null（段 2 へ縮退）。材料は全バッチに同じものが載る（分割は材料を減らさない）。
    //
    // **First-Fit Decreasing** を用いる（サイズ降順に並べ、入る**最初の**バッチへ置く）。
    // 初版は Next-Fit（現在のバッチにだけ入れ、入らなければ即座に閉じる）だったが、
    // **一度閉じたバッチの空きを二度と使わない**ため呼び出し回数が最悪 2 倍になる。
    // 呼び出し 1 回ぶんの費用は共有分（shared + 全材料）を毎回積み直すことであり、
    // **バッチ数がそのまま LLM の費用と待ち時間に効く**（IADR-0247 の目的は費用統制である）。
    //
    // 🔴 **並べ替えは割当を決めるためだけに使い、出力の順序には持ち込まない。**
    // バッチ内の銘柄は入力順、バッチ自体は含む最小添字の順に戻す。監視銘柄の並びが
    // 優先度を表す運用へ移っても、本型が黙って順序を入れ替えることはない。
    // 並べ替えの鍵は (サイズ降順, 元の添字昇順) の安定順であり、決定性は保たれる。
    private static List<List<ScreeningSymbolLoad>>? TryPack(
        int shared, IReadOnlyList<ScreeningSymbolLoad> symbols, int materialTotal, int budget)
    {
        var overhead = shared + materialTotal;

        // 単独でも収まらない銘柄が 1 つでもあれば、分割では解けない → 段 2 へ。
        if (symbols.Any(s => overhead + s.ProtectedChars > budget))
        {
            return null;
        }

        // 割当のみを決める。要素は (元の添字, 銘柄)。
        var bins = new List<List<(int Index, ScreeningSymbolLoad Symbol)>>();
        var binSizes = new List<int>();

        foreach (var (symbol, index) in symbols
                     .Select((s, i) => (Symbol: s, Index: i))
                     .OrderByDescending(x => x.Symbol.ProtectedChars)
                     .ThenBy(x => x.Index)
                     .Select(x => (x.Symbol, x.Index)))
        {
            var placed = false;
            for (var b = 0; b < bins.Count; b++)
            {
                if (binSizes[b] + symbol.ProtectedChars <= budget)
                {
                    bins[b].Add((index, symbol));
                    binSizes[b] += symbol.ProtectedChars;
                    placed = true;
                    break;
                }
            }

            if (!placed)
            {
                bins.Add([(index, symbol)]);
                binSizes.Add(overhead + symbol.ProtectedChars);
            }
        }

        // 出力は入力順へ戻す（バッチ内は添字昇順、バッチ列は各バッチの最小添字順）。
        return bins
            .OrderBy(b => b.Min(x => x.Index))
            .Select(b => b.OrderBy(x => x.Index).Select(x => x.Symbol).ToList())
            .ToList();
    }
}

/// <summary>スクリーニング対象 1 銘柄の**削れない**材料のサイズ（銘柄・市場・当日の市況・価格の行）。</summary>
public sealed record ScreeningSymbolLoad(string Symbol, int ProtectedChars);

/// <summary>削減可能な材料の種別。**保護対象（方針・市況）はこの型に現れない**（削れない）。</summary>
public enum ScreeningMaterialKind
{
    /// <summary>RAG で引いた過去の判断・振り返り（縮退の段 2 で削る）。</summary>
    RagReference,

    /// <summary>収集したニュース・開示情報（縮退の段 3 で古い順・関連度の低い順に削る）。</summary>
    NewsDisclosure,
}

/// <summary>削減可能な材料 1 件（元列の添字・概算サイズ・発行時刻・関連度）。</summary>
public sealed record ScreeningMaterial(
    int Index, ScreeningMaterialKind Kind, int Chars, DateTimeOffset? PublishedAt, double Relevance);

/// <summary>1 回のスクリーニング呼び出し（保持する銘柄と材料）。</summary>
public sealed record ScreeningBatch(
    IReadOnlyList<ScreeningSymbolLoad> Symbols,
    IReadOnlyList<ScreeningMaterial> Materials,
    bool ExceedsBudget);

/// <summary>
/// 縮退計画の結果。**分割（材料は減らない）と切り詰め（材料が減る）は分けて数える**（planning#53 の裁定・
/// 月報の件数記載の単位）。
/// </summary>
public sealed record ScreeningContextPlan(
    IReadOnlyList<ScreeningBatch> Batches,
    bool SplitOccurred,
    int DroppedRagCount,
    int DroppedNewsCount,
    bool UnresolvableOverflow)
{
    /// <summary>切り詰め（材料の削除）が発生したか。分割とは別に数える。</summary>
    public bool TruncationOccurred => DroppedRagCount + DroppedNewsCount > 0;

    /// <summary>記録すべき縮退（分割・切り詰め・解消不能な超過のいずれか）が発生したか。</summary>
    public bool ReductionOccurred => SplitOccurred || TruncationOccurred || UnresolvableOverflow;
}
