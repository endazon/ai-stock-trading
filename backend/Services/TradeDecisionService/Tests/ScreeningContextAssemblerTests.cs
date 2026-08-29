using TradeDecisionService.Domain;
using TradeDecisionService.Features.TradeDecision;
using AwesomeAssertions;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-02, FR-04, ADR-0003, #568, IADR-0247: ScreeningContextAssembler が RetrievedContext.PublishedAt を
// ScreeningMaterial へ正しく伝え、縮退段③（古い順・関連度の低い順）が実効になることを検証する。
// 縮退順序そのものの網羅は ScreeningContextPlannerTests が持つ（本テストはプランナへ渡す
// 「発行時刻の供給」だけを対象とする）。統制系のため 3 点セット（境界値/肯定・否定形）で構成する。
public class ScreeningContextAssemblerTests
{
    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 8, 29), "方針");
    private static readonly DecisionTrigger Trigger = DecisionTrigger.Scheduled("AAPL", AiStockTrading.Shared.Contracts.Trading.Market.UnitedStates);

    // 本文 100 文字で概算サイズを揃える（TradeDecisionService.Tests.ScreeningContextDegradationTests と同じ作法）。
    private static RetrievedContext News(string title, double score, DateTimeOffset? publishedAt) =>
        new(title, new string('あ', 100), SourceUri: null, score, ["google-news"], publishedAt);

    private static readonly DateTimeOffset Old = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset New = new(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 発行時刻はRetrievedContextからScreeningMaterialへそのまま伝わる()
    {
        var retrieved = new[] { News("記事", 0.5, Old) };

        var assembled = ScreeningContextAssembler.Assemble(Trigger, Policy, retrieved, currentPrice: null, budgetChars: 10_000);

        assembled.Plan.Batches.Should().ContainSingle();
        assembled.Plan.Batches[0].Materials.Should().ContainSingle().Which.PublishedAt.Should().Be(Old);
    }

    // 対の肯定形（受け入れ基準④）: 発行時刻を取得できる場合、古い記事は関連度が高くても先に削られる。
    // これが「段③が実効になった」ことを直接示す（供給が無かった旧実装では関連度だけで残余が決まっていた）。
    [Fact]
    public void 発行時刻が取れる場合は関連度が高くても古い記事が先に削られる()
    {
        var oldHighRelevance = News("古いが関連度が高い記事", score: 0.9, Old);
        var newLowRelevance = News("新しいが関連度が低い記事", score: 0.1, New);
        var retrieved = new[] { oldHighRelevance, newLowRelevance };

        // 保護分（骨格 600 + 方針 2 文字 + 銘柄行 120）= 722。材料 2 件（171+172=343）を足すと
        // 1065 > 予算 900 のため 1 件だけ削れば収まる（722+172=894 ≤ 900）。
        var assembled = ScreeningContextAssembler.Assemble(Trigger, Policy, retrieved, currentPrice: null, budgetChars: 900);

        assembled.Plan.DroppedNewsCount.Should().Be(1, "予算内に収まらない 1 件が削られる");
        var retainedTitles = assembled.RetainedReferences.Select(r => r.Title).ToList();
        retainedTitles.Should().Contain("新しいが関連度が低い記事", "新しい記事は関連度が低くても残る（古い順が優先）");
        retainedTitles.Should().NotContain("古いが関連度が高い記事", "関連度が高くても古い記事は先に削られる");
    }

    // 対の否定形（受け入れ基準④の保守側既定）: 発行時刻が取得できない材料は、これまでどおり
    // 最古扱いで先に削られる（関連度が高くても発行時刻不明であれば残らない）。
    [Fact]
    public void 発行時刻が取れない記事は関連度が高くても最古扱いで先に削られる_否定形()
    {
        var unknownHighRelevance = News("発行時刻不明だが関連度が高い記事", score: 0.9, publishedAt: null);
        var datedLowRelevance = News("発行時刻ありで関連度が低い記事", score: 0.1, New);
        var retrieved = new[] { unknownHighRelevance, datedLowRelevance };

        // 保護分 722 ＋材料 2 件（176+175=351）=1073 > 予算 900 のため 1 件だけ削れば収まる
        // （722+175=897 ≤ 900）。発行時刻不明（HasValue=false）は関連度に関わらずソート順の先頭に来る。
        var assembled = ScreeningContextAssembler.Assemble(Trigger, Policy, retrieved, currentPrice: null, budgetChars: 900);

        assembled.Plan.DroppedNewsCount.Should().Be(1);
        var retainedTitles = assembled.RetainedReferences.Select(r => r.Title).ToList();
        retainedTitles.Should().Contain("発行時刻ありで関連度が低い記事", "発行時刻を持つ記事は、関連度で劣っても発行時刻不明の記事より残る");
        retainedTitles.Should().NotContain("発行時刻不明だが関連度が高い記事", "発行時刻不明は最古扱い（保守側既定）で先に削られる");
    }
}
