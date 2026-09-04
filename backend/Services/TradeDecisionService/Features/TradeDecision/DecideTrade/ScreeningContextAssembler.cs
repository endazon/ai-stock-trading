using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Domain;

namespace TradeDecisionService.Features.TradeDecision.DecideTrade;

// FR-02, FR-04, ADR-0003, #337, IADR-0247: RAG 取得文脈からスクリーニング入力（材料）を組み、
// ScreeningContextPlanner（縮退順序の純関数）を適用して「プロンプトへ残す参考情報」を確定する。
//
// 材料の分類は出所タグ（RetrievedContext.Tags。KnowledgeBaseWriterSink が付与し RetrievalSourcePolicy が
// 同じ語彙で出典限定する）で行う。
//
//   | タグ | 分類 | 縮退での扱い |
//   | `finnhub` / `moomoo`（市況・価格）・`collection-status`（欠測の明示） | **保護** | 削らない。市況を削ると銘柄を評価できず、欠測の明示を削ると ADR-0020 が塞いだ「無言の空データ」が復活する |
//   | `report`（自リポジトリの確定報告書＝過去の判断・振り返り） | RAG | 段 2 で関連度の低い順に削る |
//   | それ以外（`finnhub-news` / `google-news` / `sec-edgar` / `edinet` / `boj` / `fred` 等） | ニュース/開示 | 段 3 で古い順・関連度の低い順に削る |
//
// **確定した日報の方針は材料ですらない**（プロンプトの骨格＝共有保護分としてサイズだけ数える）。
// 発行時刻は RetrievedContext.PublishedAt（KnowledgeHit 由来）をそのまま渡す（#568・IADR-0247 残余
// リスクの解消・IADR-0270）。取得できなかった材料は null のままであり、プランナ側の保守側既定
// （発行時刻不明＝最古扱いで先に削る）へ倒れる。
public static class ScreeningContextAssembler
{
    // 語彙は RetrievalSourcePolicy.Default（＝収集側 SourceAllowlist）と同一。市況源と欠測明示のみ保護。
    private static readonly HashSet<string> ProtectedTags =
        new(StringComparer.OrdinalIgnoreCase) { "finnhub", "moomoo", "collection-status" };

    private static readonly HashSet<string> RagTags = new(StringComparer.OrdinalIgnoreCase) { "report" };

    // プロンプト骨格（指示文・見出し・出力形式）の概算。予算は 200K トークンの文字数プロキシであり、
    // ここは精密である必要はない（過大に見積もるほど安全側＝早めに縮退する）。
    // FR-04, ADR-0016 決定11, IADR-0296: 空売りガードレール短縮版（`# 空売りの制約（結論）` 節。
    // BuildScreening が無条件で追加）の実測 142 文字ぶんを 600→750 へ底上げした（安全側の余裕込み）。
    private const int PromptScaffoldChars = 750;

    // 銘柄・市場・現在値の行の概算（保護対象）。
    private const int PerSymbolLineChars = 120;

    // 参考情報 1 件の JSON 化オーバーヘッド（キー名・引用符・フェンス）の概算。
    private const int PerReferenceOverheadChars = 60;

    public static AssembledScreeningContext Assemble(
        DecisionTrigger trigger,
        DailyPolicy policy,
        IReadOnlyList<RetrievedContext> retrieved,
        decimal? currentPrice,
        int budgetChars)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(retrieved);

        var protectedRefs = new List<RetrievedContext>();
        var reducible = new List<(RetrievedContext Reference, ScreeningMaterial Material)>();

        for (var i = 0; i < retrieved.Count; i++)
        {
            var reference = retrieved[i];
            if (reference.Tags.Any(ProtectedTags.Contains))
            {
                protectedRefs.Add(reference);
                continue;
            }

            var kind = reference.Tags.Any(RagTags.Contains)
                ? ScreeningMaterialKind.RagReference
                : ScreeningMaterialKind.NewsDisclosure;
            reducible.Add((reference, new ScreeningMaterial(
                i, kind, EstimateChars(reference), reference.PublishedAt, reference.Score)));
        }

        // 保護分: プロンプト骨格 + 方針全文（共有）と、銘柄行 + 現在値行 + 保護参考情報（銘柄側）。
        var sharedProtected = PromptScaffoldChars + policy.Summary.Length;
        var symbolProtected = PerSymbolLineChars + protectedRefs.Sum(EstimateChars);

        var plan = ScreeningContextPlanner.Plan(
            sharedProtected,
            [new ScreeningSymbolLoad(trigger.Symbol, symbolProtected)],
            reducible.Select(r => r.Material).ToList(),
            budgetChars);

        // 残す参考情報 = 保護（常に残る）+ プランナが保持した削減可能材料。元の順序（関連度順）を保つ。
        var retainedIndexes = plan.Batches.Count > 0
            ? plan.Batches[0].Materials.Select(m => m.Index).ToHashSet()
            : [];
        var retained = new List<RetrievedContext>(protectedRefs.Count + retainedIndexes.Count);
        for (var i = 0; i < retrieved.Count; i++)
        {
            var reference = retrieved[i];
            if (reference.Tags.Any(ProtectedTags.Contains) || retainedIndexes.Contains(i))
            {
                retained.Add(reference);
            }
        }

        return new AssembledScreeningContext(plan, retained, budgetChars);
    }

    // 概算サイズ。プロンプト側の上限（タイトル 200 / 本文 400 / 出典 500）と JSON 化のオーバーヘッドで見積もる。
    private static int EstimateChars(RetrievedContext reference) =>
        Math.Min(reference.Title.Length, 200)
        + Math.Min(reference.Text.Length, 400)
        + Math.Min(reference.SourceUri?.Length ?? 0, 500)
        + PerReferenceOverheadChars;

    /// <summary>縮退計画と、スクリーニングプロンプトへ残す参考情報（保護＋残余）。</summary>
    public sealed record AssembledScreeningContext(
        ScreeningContextPlan Plan,
        IReadOnlyList<RetrievedContext> RetainedReferences,
        int BudgetChars);
}
