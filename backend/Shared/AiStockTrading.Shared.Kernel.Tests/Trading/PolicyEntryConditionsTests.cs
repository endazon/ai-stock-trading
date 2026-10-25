using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Kernel.Tests.Trading;

// FR-04, FR-07, #1384, #1393, IADR-0558 決定 1（2026-10-20 追記）: 方針の書式行（「条件 X:」「X 名前:」）だけから新規建ての条件の記号を読む（自由文は読まない）。
public class PolicyEntryConditionsTests
{
    // #1384 AC-04-20: 書式行（箇条書きの印・売買の向きの語・名前の括弧・全角）から記号を集める。
    [Fact]
    public void 書式行から条件の記号を集める()
    {
        const string policy = """
            条件 A: AAPL 前日比 -3% 以下で買い
            - 買い条件 B（急落反発）: 日中安値から +1% 戻したら買い
            ・売り条件Ｃ：保有の撤退
            * 新規建て条件 D (ブレイク): 高値更新で買い
            条件 A: 同じ記号の 2 行目
            """;

        PolicyEntryConditions.Symbols(policy).Should().BeEquivalentTo(["A", "B", "C", "D"]);
    }

    // #1384 AC-04-21（否定形）: 自由文・記号が 2 字以上・コロンの無い行・小文字は読まない。空の方針は空集合。
    [Theory]
    [InlineData("条件 B で建てた建玉は安値割れで撤退する。")]
    [InlineData("条件 AB: 2 字の記号")]
    [InlineData("条件 b: 小文字")]
    [InlineData("買い条件は B: 語順違い")]
    [InlineData("")]
    [InlineData(null)]
    public void 書式に合わない行からは記号を読まない(string? policy)
    {
        PolicyEntryConditions.Symbols(policy).Should().BeEmpty();
    }

    // #1393 AC-1, IADR-0558 追記（2026-10-20）: 稼働中の方針（daily-2026-10-17 の版 5）の書式「X 名前:」から記号を集める。
    // PoC（2026-10-20 00:32 JST）では「条件 X:」しか読まず、SMCI の条件 A の記録が「方針に「条件 X:」行が無い」で不明になった。
    [Fact]
    public void 版5の方針の行頭の記号と名前の行から記号を集める()
    {
        const string policy = """
            新規建ての条件（いずれか成立で買い）:
            A 上放れ追随: 当日始値比 +2% 以上かつ日中高値を更新したら買い
            B 急落反発: 前日比 -3% 以下から日中安値を +1% 戻したら買い
            出口: 建玉の平均取得単価から -1.5% で撤退
            """;

        PolicyEntryConditions.Symbols(policy).Should().BeEquivalentTo(["A", "B"]);
    }

    // #1393: 箇条書きの印・全角の区切りと空白・「条件 X:」との混在も読む。
    [Fact]
    public void 行頭の記号の行は箇条書きの印と全角と混在を許す()
    {
        const string policy = """
            - A 上放れ追随: 高値更新で買い
            * B 急落反発：日中安値から戻したら買い
            ・Ｃ　ギャップ埋め：前日終値まで戻したら売り
            条件 D: 前日比 -5% 以下で買い
            """;

        PolicyEntryConditions.Symbols(policy).Should().BeEquivalentTo(["A", "B", "C", "D"]);
    }

    // #1393 AC-2（否定形）: 行頭の記号の書式の誤読の範囲を固定する。
    [Theory]
    [InlineData("A 社の決算が予想を上回ったため様子見とする")] // 区切りが無い
    [InlineData("前日の高値を A: として扱う")] // 文中の「A:」
    [InlineData("AI: 半導体の需要が続く")] // 2 字以上の大文字
    [InlineData("EPS: 予想を上回る")] // 2 字以上の大文字
    [InlineData("AI 銘柄: 押し目で買い")] // 記号の直後が空白でない
    [InlineData("注: A 上放れ追随: 高値更新で買い")] // 行頭でない位置
    [InlineData("買いは A 上放れ追随: 高値更新")] // 行頭でない位置
    [InlineData("A: 名前の無い行")] // 名前が無い
    [InlineData("a 上放れ追随: 小文字")] // 小文字
    [InlineData("A 上放れ追随は前日の高値を上抜けたときに買う条件である: 説明")] // 名前が 21 字以上（文）
    [InlineData("A 上放れ。追随: 句点を含む")] // 名前に句点
    [InlineData("A new high: 英文の行")] // 名前に空白（英文）
    [InlineData("- B 急落 反発: 名前の途中に空白")] // 名前に空白
    public void 行頭の記号の書式に合わない行からは記号を読まない(string policy)
    {
        PolicyEntryConditions.Symbols(policy).Should().BeEmpty();
    }

    // #1393, IADR-0558 追記（2026-10-20）: 🔴 許容する残余を固定する。空白の無い日本語で 20 字以内に「:」がある自由文は、書式だけでは見分けられず読まれる。
    // 影響は限定的: 集合は判断が答えた記号の実在の確かめにだけ使い、判断がこの記号を答えない限り記録は変わらない。この挙動が変わったら追記を見直す。
    [Fact]
    public void 空白の無い日本語の自由文で20字以内に区切りがある行は読まれる許容する残余()
    {
        PolicyEntryConditions.Symbols("A 社の決算は好調: 様子見とする").Should().BeEquivalentTo(["A"]);
    }

    // #1384 AC-04-22: 判断が答えた記号を英大文字 1 字へ寄せる。寄せられなければ null。
    [Theory]
    [InlineData("B", "B")]
    [InlineData(" b ", "B")]
    [InlineData("Ｂ", "B")]
    [InlineData("BB", null)]
    [InlineData("1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void 答えた記号を英大文字1字へ寄せる(string? answered, string? expected)
    {
        PolicyEntryConditions.NormalizeSymbol(answered).Should().Be(expected);
    }
}
