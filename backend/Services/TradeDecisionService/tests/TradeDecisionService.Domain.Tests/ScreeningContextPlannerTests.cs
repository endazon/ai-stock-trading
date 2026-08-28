using AiStockTrading.TradeDecision.Domain;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.TradeDecision.Domain.Tests;

// FR-02, FR-04, ADR-0003, #337, IADR-0247: スクリーニング入力の縮退順序（① 銘柄分割 → ② RAG 削除 →
// ③ ニュース/開示を古い順・関連度の低い順に削除）の検証。
//
// 統制系（判断材料の統制）のため 3 点セットで構成する。
//   1. 境界値テーブル — 予算に対する各段の発動境界
//   2. プロパティベース — 保護対象（銘柄の保護分）は常に全バッチに残る／カウンタ＝実削除数の不変条件
//   3. 否定形 — 全段を使い切っても保護対象を削る計画を返さない（UnresolvableOverflow で返す）
public class ScreeningContextPlannerTests
{
    private static ScreeningSymbolLoad Sym(string name, int chars) => new(name, chars);

    private static ScreeningMaterial Rag(int index, int chars, double relevance = 0.5) =>
        new(index, ScreeningMaterialKind.RagReference, chars, PublishedAt: null, relevance);

    private static ScreeningMaterial News(int index, int chars, DateTimeOffset? publishedAt, double relevance = 0.5) =>
        new(index, ScreeningMaterialKind.NewsDisclosure, chars, publishedAt, relevance);

    private static readonly DateTimeOffset T0 = new(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);

    // --- 1. 境界値テーブル ---

    [Fact]
    public void 予算内なら分割も切り詰めもしない()
    {
        var plan = ScreeningContextPlanner.Plan(
            sharedProtectedChars: 100,
            [Sym("AAPL", 50), Sym("MSFT", 50)],
            [Rag(0, 100), News(1, 100, T0)],
            budgetChars: 400);

        plan.Batches.Should().ContainSingle();
        plan.SplitOccurred.Should().BeFalse();
        plan.TruncationOccurred.Should().BeFalse();
        plan.ReductionOccurred.Should().BeFalse();
        plan.Batches[0].Symbols.Should().HaveCount(2);
        plan.Batches[0].Materials.Should().HaveCount(2);
    }

    [Fact]
    public void 段1_超過したらまず銘柄を分割し材料は減らさない()
    {
        // shared(100) + materials(200) + 銘柄 1 つ(50) = 350 ≤ 360 だが、2 銘柄では 400 > 360。
        var plan = ScreeningContextPlanner.Plan(
            100,
            [Sym("AAPL", 50), Sym("MSFT", 50)],
            [Rag(0, 100), News(1, 100, T0)],
            budgetChars: 360);

        plan.SplitOccurred.Should().BeTrue();
        plan.Batches.Should().HaveCount(2);
        plan.TruncationOccurred.Should().BeFalse("分割は材料を減らさない（計画の表・段 1）");
        plan.Batches.Should().AllSatisfy(b => b.Materials.Should().HaveCount(2, "全バッチが全材料を保つ"));
        plan.Batches.SelectMany(b => b.Symbols).Select(s => s.Symbol)
            .Should().BeEquivalentTo(["AAPL", "MSFT"], "分割で銘柄は失われない");
    }

    [Fact]
    public void 段2_分割しても収まらなければRAGを関連度の低い順に削る()
    {
        // 1 銘柄でも shared(100) + 銘柄(50) + 材料(300) = 450 > 300。
        // RAG 2 件（関連度 0.2 / 0.8）のうち低い方から削る。0.2 を削って 350 > 300、0.8 も削って 250 ≤ 300。
        var plan = ScreeningContextPlanner.Plan(
            100,
            [Sym("AAPL", 50)],
            [Rag(0, 100, relevance: 0.8), Rag(1, 100, relevance: 0.2), News(2, 100, T0)],
            budgetChars: 300);

        plan.DroppedRagCount.Should().Be(2);
        plan.DroppedNewsCount.Should().Be(0, "RAG で収まったらニュースへ手を付けない（②→③ の順）");
        plan.TruncationOccurred.Should().BeTrue();
        plan.Batches.Should().ContainSingle();
        plan.Batches[0].Materials.Should().ContainSingle().Which.Kind.Should().Be(ScreeningMaterialKind.NewsDisclosure);
    }

    [Fact]
    public void 段2_RAGは関連度の低い順に削り高いものを残す()
    {
        // 1 件だけ削れば収まる予算。関連度 0.2 が削られ 0.8 が残る。
        var plan = ScreeningContextPlanner.Plan(
            100,
            [Sym("AAPL", 50)],
            [Rag(0, 100, relevance: 0.8), Rag(1, 100, relevance: 0.2)],
            budgetChars: 250);

        plan.DroppedRagCount.Should().Be(1);
        plan.Batches[0].Materials.Should().ContainSingle().Which.Relevance.Should().Be(0.8);
    }

    [Fact]
    public void 段3_RAGを削り切っても収まらなければニュースを古い順に削る()
    {
        // shared(100)+銘柄(50)+材料(400)=550 > 250。RAG(100) を削って 450、古いニュース(100)→350、
        // 新しいニュース(100)→250 ≤ 250 で停止。最新の 1 件が残る。
        var newest = News(3, 100, T0.AddHours(2));
        var plan = ScreeningContextPlanner.Plan(
            100,
            [Sym("AAPL", 50)],
            [Rag(0, 100), News(1, 100, T0), News(2, 100, T0.AddHours(1)), newest],
            budgetChars: 250);

        plan.DroppedRagCount.Should().Be(1);
        plan.DroppedNewsCount.Should().Be(2);
        plan.Batches[0].Materials.Should().ContainSingle().Which.Should().Be(newest, "新しいものから残す（古い順に削る）");
    }

    [Fact]
    public void 段3_発行時刻不明のニュースは最古として先に削る()
    {
        var dated = News(1, 100, T0);
        var plan = ScreeningContextPlanner.Plan(
            100,
            [Sym("AAPL", 50)],
            [News(0, 100, publishedAt: null), dated],
            budgetChars: 250);

        plan.DroppedNewsCount.Should().Be(1);
        plan.Batches[0].Materials.Should().ContainSingle().Which.Should().Be(dated);
    }

    [Fact]
    public void 段3_同時刻のニュースは関連度の低い順に削る()
    {
        var relevant = News(1, 100, T0, relevance: 0.9);
        var plan = ScreeningContextPlanner.Plan(
            100,
            [Sym("AAPL", 50)],
            [News(0, 100, T0, relevance: 0.1), relevant],
            budgetChars: 250);

        plan.DroppedNewsCount.Should().Be(1);
        plan.Batches[0].Materials.Should().ContainSingle().Which.Should().Be(relevant);
    }

    // --- 3. 否定形 ---

    [Fact]
    public void 全段を使い切っても保護対象は削らず超過のまま返す_否定形()
    {
        // 保護対象（shared 300 + 銘柄 100 = 400）だけで予算 300 を超える。材料はすべて削られるが、
        // 保護対象は削れない（型として不可能）＝ UnresolvableOverflow を立てて返す。
        var plan = ScreeningContextPlanner.Plan(
            300,
            [Sym("AAPL", 100)],
            [Rag(0, 50), News(1, 50, T0)],
            budgetChars: 300);

        plan.UnresolvableOverflow.Should().BeTrue();
        plan.Batches.Should().ContainSingle();
        plan.Batches[0].ExceedsBudget.Should().BeTrue();
        // 保護対象（銘柄）はバッチに残っている——「削って収める」選択肢は存在しない。
        plan.Batches[0].Symbols.Should().ContainSingle().Which.Symbol.Should().Be("AAPL");
    }

    [Fact]
    public void 縮退計画は上位モデルという概念を持たない_否定形()
    {
        // 利用者裁定 2026-08-02: 上位モデルへの退避は採らない。プランナの公開面にモデルを表す
        // 型・文字列が存在しないことを固定する（退避の選択肢が構造的に無い）。
        var members = typeof(ScreeningContextPlan).GetProperties()
            .Concat(typeof(ScreeningBatch).GetProperties())
            .Select(p => p.Name);

        members.Should().NotContain(
            name => name.Contains("Model", StringComparison.OrdinalIgnoreCase),
            "縮退はモデル退避ではなく入力の切り詰めで行う（planning#53 の裁定）");
    }

    // --- 2. プロパティベース（決定的な全組み合わせ走査） ---

    [Fact]
    public void 任意の入力で保護対象は常に全バッチへ残りカウンタは実削除数と一致する()
    {
        var budgets = new[] { 150, 250, 400, 700, 10_000 };
        var symbolSets = new[]
        {
            new[] { Sym("A", 40) },
            new[] { Sym("A", 40), Sym("B", 80), Sym("C", 20) },
            new[] { Sym("A", 200), Sym("B", 200) },
        };
        var materialSets = new[]
        {
            Array.Empty<ScreeningMaterial>(),
            [Rag(0, 60, 0.3), News(1, 90, T0), News(2, 30, null, 0.9)],
            [Rag(0, 300, 0.1), Rag(1, 100, 0.9), News(2, 250, T0.AddDays(-1)), News(3, 50, T0)],
        };

        foreach (var budget in budgets)
            foreach (var symbols in symbolSets)
                foreach (var materials in materialSets)
                {
                    var plan = ScreeningContextPlanner.Plan(100, symbols, materials, budget);

                    // 不変条件 1: 銘柄（保護対象）は 1 つも失われず、ちょうど 1 バッチに属する。
                    plan.Batches.SelectMany(b => b.Symbols).Select(s => s.Symbol)
                        .Should().BeEquivalentTo(symbols.Select(s => s.Symbol),
                            "縮退は保護対象（銘柄の市況・価格）を削らない");

                    // 不変条件 2: カウンタ＝実際に削られた材料数（分割と切り詰めは別勘定）。
                    var retained = plan.Batches.Count > 0 ? plan.Batches[0].Materials.Count : materials.Length;
                    (plan.DroppedRagCount + plan.DroppedNewsCount).Should().Be(materials.Length - retained);

                    // 不変条件 3: 削られるのは削減可能種別のみ（保持列は元材料の部分集合）。
                    if (plan.Batches.Count > 0)
                    {
                        plan.Batches[0].Materials.Should().BeSubsetOf(materials);
                    }

                    // 不変条件 4: 解消不能でない限り、全バッチが予算内。
                    if (!plan.UnresolvableOverflow)
                    {
                        plan.Batches.Should().AllSatisfy(b => b.ExceedsBudget.Should().BeFalse());
                    }
                }
    }
}
