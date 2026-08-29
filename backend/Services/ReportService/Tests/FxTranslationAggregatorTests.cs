using ReportService.Domain;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-16, #338, 04_report-templates §数値の定義（為替差損益）, IADR-0251:
// 為替差損益の集計（純関数）を固定する。
//
// 🔴 計画の明文: 「円換算により生じた損益。**取引損益と混ぜず独立した行として表示する**。」
// 🔴 **数値はコードで集計し LLM に計算させない**（FR-16）。本関数が唯一の算出経路である。
public class FxTranslationAggregatorTests
{
    // --- 境界値テーブル ---

    [Theory]
    // 額 / 認識時レート / 期末レート / 期待する為替差損益（円）
    [InlineData(100, 150, 150, 0)]      // レート不変 → 0（境界）
    [InlineData(100, 150, 160, 1000)]   // 円安 → 円換算で得
    [InlineData(100, 160, 150, -1000)]  // 円高 → 円換算で損
    [InlineData(-100, 150, 160, -1000)] // 額が負（損失）なら符号が反転する
    [InlineData(0, 150, 160, 0)]        // 額 0 → 0（境界）
    public void 為替差損益は額とレート差の積である(decimal amount, decimal atRecognition, decimal atEnd, decimal expected)
    {
        var s = FxTranslationAggregator.Aggregate([new FxTranslationEntry(amount, atRecognition, atEnd)]);

        s.TranslationGainJpy.Should().Be(expected);
        s.EntryCount.Should().Be(1);
    }

    // 🔴 **プロパティ**: レートが等しければ、金額がいくらであっても為替差損益は必ず 0 である。
    // 「為替の変動が無ければ為替差損益は生じない」という不変条件であり、
    // この性質が壊れると取引損益が為替差損益へ漏れている（＝混ぜている）ことを意味する。
    [Fact]
    public void レートが不変なら金額によらず為替差損益はゼロである()
    {
        var rng = new Random(20260828); // 決定的（種を固定する）
        for (var i = 0; i < 500; i++)
        {
            var amount = (decimal)(rng.NextDouble() * 20_000 - 10_000);
            var rate = (decimal)(rng.NextDouble() * 200 + 50);

            FxTranslationAggregator.Aggregate([new FxTranslationEntry(amount, rate, rate)])
                .TranslationGainJpy.Should().Be(0m);
        }
    }

    // 🔴 **プロパティ**: 認識時と期末を入れ替えると符号だけが反転する（対称性）。
    [Fact]
    public void レートを入れ替えると符号だけが反転する()
    {
        var rng = new Random(20260829);
        for (var i = 0; i < 500; i++)
        {
            var amount = (decimal)(rng.NextDouble() * 20_000 - 10_000);
            var a = (decimal)(rng.NextDouble() * 200 + 50);
            var b = (decimal)(rng.NextDouble() * 200 + 50);

            var forward = FxTranslationAggregator.Aggregate([new FxTranslationEntry(amount, a, b)]).TranslationGainJpy;
            var backward = FxTranslationAggregator.Aggregate([new FxTranslationEntry(amount, b, a)]).TranslationGainJpy;

            backward.Should().Be(-forward);
        }
    }

    [Fact]
    public void 複数明細は合算し件数を保持する()
    {
        var s = FxTranslationAggregator.Aggregate(
        [
            new FxTranslationEntry(100m, 150m, 160m),  // +1,000
            new FxTranslationEntry(50m, 160m, 150m),   //   -500
        ]);

        s.TranslationGainJpy.Should().Be(500m);
        s.EntryCount.Should().Be(2);
    }

    // 明細が無い期間は 0 円・0 件である。**これは「供給が無い」とは別の状態**であり、
    // 未供給は `FxTranslationSummary` そのものが null であることで表す（描画側のテストで固定する）。
    [Fact]
    public void 明細が無ければゼロ円ゼロ件である()
    {
        var s = FxTranslationAggregator.Aggregate([]);

        s.TranslationGainJpy.Should().Be(0m);
        s.EntryCount.Should().Be(0);
    }
}
