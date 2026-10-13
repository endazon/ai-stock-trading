using AiStockTrading.Shared.Contracts.Logging;
using TradeDecisionService.Domain;
using AwesomeAssertions;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-04, FR-11, #1332, IADR-0531: 一次スクリーニングの NoJsonObject（PoC 2026-10-12・2/682・切り詰めではない）の原因の候補を
// 原本なしで絞り込む特性試験と、原本の保全に使う形の指紋・抜粋の検証。
public class UnparseableOutputShapeTests
{
    // 🔴 IADR-0531 決定 2（除外）: 次の形は NoJsonObject に**ならない**。抽出は最初の '{' から深さ 0 までを取るため、
    // 前置き・後置きの散文・コードフェンス・文字列の中の括弧・2 個目の JSON は通る。抽出を変えたらここが赤くなる。
    [Theory]
    [InlineData("判断します。\n{\"action\":\"Hold\",\"rationale\":\"材料なし\"}", TradeAction.Hold)]
    [InlineData("```json\n{\"action\":\"Buy\",\"rationale\":\"上昇\"}\n```", TradeAction.Buy)]
    [InlineData("{\"action\":\"Hold\",\"rationale\":\"レンジ {上限} を} 超えない\"}\n以上です。}", TradeAction.Hold)]
    [InlineData("{\"action\":\"Sell\",\"rationale\":\"反落\"}\n{\"action\":\"Buy\",\"rationale\":\"別案\"}", TradeAction.Sell)]
    [InlineData("{\"action\":\"Hold\",\"rationale\":\"全角の文字・「括弧」・｛全角｝・監視銘柄\"}", TradeAction.Hold)]
    public void 前置きフェンス文字列内の括弧複数のJSONはNoJsonObjectにならない(string output, TradeAction expected)
    {
        var screening = TradeDecisionParser.ParseScreening(output);

        screening.IsUnparseable.Should().BeFalse();
        screening.Action.Should().Be(expected, "最初の JSON オブジェクトを読む（2 個目は拾わない）");
        TradeDecisionParser.DescribeShape(output).Suspect.Should().Be(UnparseableOutputSuspect.NotApplicable);
    }

    // IADR-0531 決定 2（除外）: 雛形の書き写しは MalformedJson（種別が違う）。抽出はできているので指紋は NotApplicable。
    [Fact]
    public void 出力形式の雛形の書き写しはMalformedJsonでありNoJsonObjectではない()
    {
        const string output = "{\"action\":\"Buy|Sell|Hold\",\"rationale\":\"絞り込み理由\",\"referencePrice\":参照価格,\"stopLossDistancePerShare\":損切り幅}";

        TradeDecisionParser.ParseScreening(output).Failure!.Kind.Should().Be(TradeDecisionParseFailureKind.MalformedJson);
        TradeDecisionParser.DescribeShape(output).Suspect.Should().Be(UnparseableOutputSuspect.NotApplicable);
    }

    // 🔴 IADR-0531 決定 2（候補 A〜E）: NoJsonObject になる形と、形の指紋が示す候補。
    // D（文字列が閉じない）は JsonDocument なら MalformedJson になる形だが、抽出の走査が末尾まで文字列の内側に留まるため NoJsonObject になる。
    [Theory]
    // A: 散文だけ（拒否・説明）。B（JSON が 2 個目の text ブロック）も AST から見ると同じ形（末尾が「以下のとおりです：」等）。
    [InlineData("この銘柄は方針の範囲外のため見送ります。", UnparseableOutputSuspect.NoBrace)]
    [InlineData("分析の結果、関心ありと判断しました。以下が出力です：", UnparseableOutputSuspect.NoBrace)]
    // C: 全角の括弧で書いた JSON。
    [InlineData("｛\"action\":\"Hold\",\"rationale\":\"材料なし\"｝", UnparseableOutputSuspect.FullWidthBraces)]
    // D: エスケープされない '"' が奇数個（語の化けが '"' を含むトークンを出した・根拠文に素の '"'）。
    [InlineData("{\"action\":\"Hold\",\"rationale\":\"監視銘\"柄に材料なし\"}", UnparseableOutputSuspect.UnterminatedString)]
    [InlineData("{\"action\":\"Buy\",\"rationale\":\"5\" の上抜け\"}", UnparseableOutputSuspect.UnterminatedString)]
    // E: JSON より前の散文に閉じない '{'、または閉じ '}' の欠落。
    [InlineData("集合 {A, B の比較:\n{\"action\":\"Hold\",\"rationale\":\"材料なし\"}", UnparseableOutputSuspect.UnclosedBrace)]
    [InlineData("{\"action\":\"Hold\",\"rationale\":\"材料なし\"\n```", UnparseableOutputSuspect.UnclosedBrace)]
    public void NoJsonObjectになる形と形の指紋が示す候補(string output, UnparseableOutputSuspect expected)
    {
        var screening = TradeDecisionParser.ParseScreening(output);

        screening.Failure!.Kind.Should().Be(TradeDecisionParseFailureKind.NoJsonObject);
        screening.Action.Should().Be(TradeAction.Hold, "挙動は従来どおり安全側（打ち切り）");
        TradeDecisionParser.DescribeShape(output).Suspect.Should().Be(expected);
    }

    // 指紋の各値（PoC で原本の行から読む値）を固定する。
    [Fact]
    public void 形の指紋は括弧引用符フェンス改行と走査の終わり方を数える()
    {
        const string output = "前置き\n```json\n{\"action\":\"Hold\",\"rationale\":\"a\\\"b\"\n```\n";

        var shape = TradeDecisionParser.DescribeShape(output);

        shape.Should().Be(new UnparseableOutputShape(
            Length: output.Length, OpenBraces: 1, CloseBraces: 0, FullWidthBraces: 0, Quotes: 9, CodeFences: 2, Newlines: 4,
            FirstBraceIndex: output.IndexOf('{'), ObjectClosed: false, EndedInString: false, DepthAtEnd: 1));
        shape.Suspect.Should().Be(UnparseableOutputSuspect.UnclosedBrace, "エスケープされた '\"' は文字列を閉じない");
        shape.Describe().Should().Be(
            $"suspect=UnclosedBrace length={output.Length} braces=1/0 fullWidthBraces=0 quotes=9 fences=2 newlines=4 "
            + $"firstBrace={output.IndexOf('{')} endedInString=False depthAtEnd=1");
    }

    [Fact]
    public void 形の指紋はnullと空でも作れる()
    {
        TradeDecisionParser.DescribeShape(null).Suspect.Should().Be(UnparseableOutputSuspect.NoBrace);
        TradeDecisionParser.DescribeShape(string.Empty).Length.Should().Be(0);
    }

    // IADR-0531 決定 1: 抜粋は先頭 1,000 字＋末尾 500 字＋省略の注記。短ければ全文。
    [Fact]
    public void 抜粋は短ければ全文を返す()
    {
        var text = new string('あ', UnparseableOutputExcerpt.HeadChars + UnparseableOutputExcerpt.TailChars);

        UnparseableOutputExcerpt.Of(text).Should().Be(text);
        UnparseableOutputExcerpt.Of(null).Should().BeEmpty();
    }

    [Fact]
    public void 抜粋は長ければ先頭と末尾を残して中央を省く()
    {
        var text = new string('H', 1_200) + new string('M', 3_000) + "{\"action\":\"Hold\"}" + new string('T', 400);

        var excerpt = UnparseableOutputExcerpt.Of(text);

        excerpt.Should().StartWith(new string('H', 1_000));
        excerpt.Should().EndWith("{\"action\":\"Hold\"}" + new string('T', 400), "末尾の閉じ括弧の有無が見える");
        excerpt.Should().Contain($"…(中略 {text.Length - 1_500} 文字)…");
        excerpt.Length.Should().BeLessThanOrEqualTo(1_500 + 32, "量の上限（IADR-0531 決定 1）");
    }

    [Fact]
    public void 抜粋はサロゲートペアを割らない()
    {
        // 先頭の境界（1,000 字目）と末尾の境界（末尾から 500 字目）にサロゲートペアを跨がせる。
        var text = new string('a', 999) + "😀" + new string('b', 2_000) + "😀" + new string('c', 499);

        var excerpt = UnparseableOutputExcerpt.Of(text);

        for (var i = 0; i < excerpt.Length; i++)
        {
            if (char.IsHighSurrogate(excerpt[i]))
            {
                char.IsLowSurrogate(excerpt[i + 1]).Should().BeTrue();
            }
            else if (char.IsLowSurrogate(excerpt[i]))
            {
                char.IsHighSurrogate(excerpt[i - 1]).Should().BeTrue();
            }
        }

        // 1 行化（呼び出し側が 1 回だけ掛ける）の後も上限の内側に収まり、行は割れない。
        var line = LogSanitizer.Sanitize(excerpt)!;
        line.Should().NotContain("\n").And.NotContain("truncated");
    }
}
