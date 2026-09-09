using AiStockTrading.Shared.Contracts.Logging;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// NFR, IADR-0316, #708（MSP#1015 の受け皿）: 外部由来文字列をログ行へ落とす前の正規化（CWE-117・log forging）。
// **「行として成立させない」ことと「本文を消さない」ことの両方**を固定する —— 値を落とす実装にすると
// IADR-0061 決定1（プロンプト・生出力の全量記録）の目的そのものが失われる。
public class LogSanitizerTests
{
    private const char Escape = (char)0x1B;
    private const char Bell = (char)0x07;
    private const char Nul = (char)0x00;
    private const char NextLine = (char)0x85;    // C1。char.IsControl が拾う
    private const char LineSeparator = '\u2028'; // 非制御だが行区切りとして働く
    private const char ParagraphSeparator = '\u2029';

    // ── 境界値テーブル: 行を割り得る文字はすべて置換される ──────────────────────────
    [Theory]
    [InlineData('\n')]
    [InlineData('\r')]
    [InlineData('\t')]
    [InlineData(Escape)]
    [InlineData(Bell)]
    [InlineData(Nul)]
    [InlineData(NextLine)]
    [InlineData(LineSeparator)]
    [InlineData(ParagraphSeparator)]
    public void 行を割り得る文字は置換される(char dangerous)
    {
        var result = LogSanitizer.Sanitize($"前{dangerous}後");

        result.Should().Be($"前{LogSanitizer.Replacement}後");
        result.Should().NotContain(dangerous.ToString());
    }

    [Fact]
    public void 偽の行の注入が_1_行へ潰れる()
    {
        // 攻撃の形: 生出力に改行と偽のログ行を仕込む。
        var forged = "正常な応答\n2026-09-09 12:00:00 [INF] 取引ガードを解除しました";

        var result = LogSanitizer.Sanitize(forged);

        result.Should().NotContain("\n");
        result!.Split('\n').Should().ContainSingle("ログ行が割れてはならない");
        // 🔴 陽性対照（消して逃げていないこと）: 本文は読める形で残る。
        result.Should().Contain("正常な応答");
        result.Should().Contain("取引ガードを解除しました");
    }

    [Fact]
    public void 危険でない文字は原文のまま残る()
    {
        const string Text = "本日は堅調でした。AAPL +1.2% 「括弧」 <tag> \"quote\" 絵文字🙂";

        LogSanitizer.Sanitize(Text).Should().Be(Text);
    }

    // ── 長さ上限 ───────────────────────────────────────────────────────────────
    [Fact]
    public void 上限以下は切られない()
    {
        var text = new string('a', 10);

        LogSanitizer.Sanitize(text, maxLength: 10).Should().Be(text);
    }

    [Fact]
    public void 上限超過は切られ落とした文字数が明示される()
    {
        var text = new string('a', 13);

        LogSanitizer.Sanitize(text, maxLength: 10).Should().Be(new string('a', 10) + "…(truncated 3 chars)");
    }

    [Fact]
    public void 既定の上限は_4000_文字である()
    {
        LogSanitizer.DefaultMaxLength.Should().Be(4000);

        var result = LogSanitizer.Sanitize(new string('a', LogSanitizer.DefaultMaxLength + 1));

        result.Should().EndWith("…(truncated 1 chars)");
    }

    [Fact]
    public void 切り詰めはサロゲートペアを割らない()
    {
        // "🙂" は 2 char。上限 3 で切ると 2 文字目（高サロゲート）で割れてしまう位置になる。
        var result = LogSanitizer.Sanitize("ab🙂cd", maxLength: 3);

        result.Should().Be("ab…(truncated 4 chars)");
        char.IsSurrogate(result![1]).Should().BeFalse("不正な UTF-16 を作らない");
    }

    [Fact]
    public void 切り詰めても制御文字は残らない()
    {
        var result = LogSanitizer.Sanitize("a\nb\nc\nd\ne", maxLength: 5);

        result.Should().Be($"a{LogSanitizer.Replacement}b{LogSanitizer.Replacement}c…(truncated 4 chars)");
    }

    // ── 否定形・境界 ────────────────────────────────────────────────────────────
    [Fact]
    public void null_は_null_のまま返す() => LogSanitizer.Sanitize(null).Should().BeNull();

    [Fact]
    public void 空文字は空文字のまま返す() => LogSanitizer.Sanitize("").Should().BeEmpty();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 上限に_1_未満を与えると例外になる(int maxLength) =>
        FluentActions.Invoking(() => LogSanitizer.Sanitize("abc", maxLength))
            .Should().Throw<ArgumentOutOfRangeException>();

    // 陽性対照（検査そのものが空振りしていないこと）: **正規化を通さなければ**改行は残る。
    // これが無いと「そもそも改行を含まない入力を使っていた」場合でも上のテストは緑になる。
    [Fact]
    public void 陽性対照_正規化を通さなければ改行は残る()
    {
        const string Forged = "正常な応答\n偽の行";

        Forged.Should().Contain("\n");
        Forged.Split('\n').Should().HaveCount(2);
    }
}
