using TradeDecisionService.Domain;
using AwesomeAssertions;
using Xunit;

namespace TradeDecisionService.Tests;

// 🔴 FR-04, FR-11, #1290, IADR-0524 決定 2: 根拠文の文字化けの疑いの検出（純関数）。
// 陽性は PoC（2026-10-06〜09）の一次スクリーニングの実測の形。陰性は正当な英字（銘柄・略語・出力形式の語・URL・数値・全角英数字）を含む日本語。
public class RationaleGarbleDetectorTests
{
    // T-10-2512: 実測の化け（置換文字・漢字の並びの中の唐突な英字列・キリル文字列）を疑う。
    [Theory]
    [InlineData("監視銘\uFFFDに含まれるが材料が乏しいため見送り")]
    [InlineData("監視銘HeaderItemに含まれるが材料が乏しいため見送り")]
    [InlineData("監視銘româ内の銘柄だが方向感が無い")]
    [InlineData("監視銘събњектに含まれるが様子見")]
    [InlineData("監視銘събњект。様子見")]
    [InlineData("監視銘glに含まれるが様子見")]
    [InlineData("監視銘heで新規の材料なし")]
    [InlineData("\uFFFD")]
    public void T_10_2512_実測の化けの形を疑う(string rationale)
    {
        RationaleGarbleDetector.IsSuspected(rationale).Should().BeTrue();
    }

    // T-10-2513: 正当な英字を含む日本語は疑わない（誤検出を避ける）。
    [Theory]
    [InlineData("AAPL はウォッチリストに含まれるが材料が乏しいため Hold")]
    [InlineData("AAPLは監視対象だが材料なし")]
    [InlineData("銘柄AAPLは押し目の兆しがない")]
    [InlineData("指標RSIが過熱圏のため見送り")]
    [InlineData("段階S1では新規建てを控える")]
    [InlineData("決算Q3の発表待ち")]
    [InlineData("銘柄BRK.Bは対象外")]
    [InlineData("判断Holdとする")]
    [InlineData("方針Buyの条件を満たさない")]
    [InlineData("損切り幅stopLossDistancePerShareを設定")]
    [InlineData("記事https://example.com/newsによれば好材料")]
    [InlineData("前日比+1.25%、出来高は20日平均比0.8倍")]
    [InlineData("株価200ドルを割り込んだ")]
    [InlineData("大手eコマースの決算が好調")]
    [InlineData("ＡＡＰＬは全角でもｈｏｌｄ扱い")]
    [InlineData("This is an English rationale with no Japanese.")]
    [InlineData("保有中の Microsoft 株は利確の基準に達していない")]
    [InlineData("監視銘牌に含まれる")] // 有効な漢字への化けは検出できない（限界。固定文の言い換えで減らす）
    [InlineData("監視銘gl。")] // 化けた英字列の後ろが句読点なら検出しない（両側を要求する限界）
    [InlineData("")]
    [InlineData(null)]
    public void T_10_2513_正当な英字を含む日本語は疑わない(string? rationale)
    {
        RationaleGarbleDetector.IsSuspected(rationale).Should().BeFalse();
    }

    // T-10-2514: 目印は前置するだけで原文を書き換えない。疑いなしは原文のまま。二重に付けない。
    [Fact]
    public void T_10_2514_目印は原文の前に1回だけ付ける()
    {
        const string garbled = "監視銘HeaderItemに含まれるが様子見";

        var marked = RationaleGarbleDetector.Mark(garbled, suspected: true);

        marked.Should().Be($"{RationaleGarbleDetector.Marker}: {garbled}");
        marked.Should().EndWith(garbled, "LLM の文言は書き換えない");
        RationaleGarbleDetector.Mark(marked, suspected: true).Should().Be(marked, "二重に付けない");
        RationaleGarbleDetector.Mark(garbled, suspected: false).Should().Be(garbled);
        RationaleGarbleDetector.Marker.Should().Be("⚠ 判断理由に文字化けの疑い");
    }
}
