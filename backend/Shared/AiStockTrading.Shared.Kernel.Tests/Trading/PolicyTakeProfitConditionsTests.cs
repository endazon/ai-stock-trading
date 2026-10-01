using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Kernel.Tests.Trading;

// FR-04, FR-07, ADR-0003, #1129, IADR-0470 決定 2・3（2026-10-01 追記 / #1129 再監査）: 方針の決まった書式の「利確:」行だけを読み、
// 自由文からは読まない。書式に合わない「利確:」行が 1 行でもあれば方針全体を読まない。同じ銘柄の行が複数あればすべてに達したときだけ到達。
// T-10-1880〜1883・T-10-1892〜1896・T-10-1942〜1944・T-10-1947〜1949。
public class PolicyTakeProfitConditionsTests
{
    private static PolicyTakeProfitCondition Pct(string? symbol, decimal threshold, decimal? partial = null) =>
        new(symbol, TakeProfitThresholdKind.GainPercent, threshold, null, partial);

    private static PolicyTakeProfitCondition Price(string? symbol, decimal threshold, Currency currency = Currency.Usd, decimal? partial = null) =>
        new(symbol, TakeProfitThresholdKind.Price, threshold, currency, partial);

    // ---- T-10-1880: 書式どおりの行を読む ----

    [Theory]
    [InlineData("利確: AAPL +5%", "AAPL", TakeProfitThresholdKind.GainPercent, "5", null, null)]
    [InlineData("利確: AAPL +4.5% (50%)", "AAPL", TakeProfitThresholdKind.GainPercent, "4.5", null, "50")]
    [InlineData("利確: MSFT $450", "MSFT", TakeProfitThresholdKind.Price, "450", Currency.Usd, null)]
    [InlineData("利確: MSFT $ 412.5 (33.3%)", "MSFT", TakeProfitThresholdKind.Price, "412.5", Currency.Usd, "33.3")]
    [InlineData("利確: BRK.B 450 ドル", "BRK.B", TakeProfitThresholdKind.Price, "450", Currency.Usd, null)]
    [InlineData("利確: BRK.B 450ドル(100%)", "BRK.B", TakeProfitThresholdKind.Price, "450", Currency.Usd, "100")]
    [InlineData("利確: T 3000円", "T", TakeProfitThresholdKind.Price, "3000", Currency.Jpy, null)]
    [InlineData("利確: 全銘柄 +8%", null, TakeProfitThresholdKind.GainPercent, "8", null, null)]
    [InlineData("- 利確: AAPL +5%", "AAPL", TakeProfitThresholdKind.GainPercent, "5", null, null)]
    [InlineData("* 利確: AAPL +5%", "AAPL", TakeProfitThresholdKind.GainPercent, "5", null, null)]
    [InlineData("・利確: AAPL +5%", "AAPL", TakeProfitThresholdKind.GainPercent, "5", null, null)]
    [InlineData("   利確:AAPL   +5 %   ", "AAPL", TakeProfitThresholdKind.GainPercent, "5", null, null)]
    public void 書式どおりの行を読む(
        string line, string? symbol, TakeProfitThresholdKind kind, string threshold, Currency? currency, string? partial)
    {
        var expected = new PolicyTakeProfitCondition(
            symbol, kind, decimal.Parse(threshold, System.Globalization.CultureInfo.InvariantCulture), currency,
            partial is null ? null : decimal.Parse(partial, System.Globalization.CultureInfo.InvariantCulture));

        // #1129 第 4 回監査 R1: 説明の文に「利確」の語があると方針全体を読まないため、挟む説明の文はその語を含まない。
        PolicyTakeProfitConditions.Parse($"押し目買いを優先する。\n{line}\n含み益が十分なら売却を検討する。")
            .Should().ContainSingle().Which.Should().Be(expected);
        PolicyTakeProfitConditions.HasAny(line).Should().BeTrue();
    }

    [Fact]
    public void 複数の行とカンマ区切りの銘柄を出現順に読む()
    {
        const string policy = "方針の説明。\r\n利確: AAPL,MSFT +5%\n利確: 全銘柄 +8%\r利確: NVDA, AMZN $120 (50%)";

        PolicyTakeProfitConditions.Parse(policy).Should().Equal(
            Pct("AAPL", 5m), Pct("MSFT", 5m), Pct(null, 8m), Price("NVDA", 120m, partial: 50m), Price("AMZN", 120m, partial: 50m));
    }

    // ---- T-10-1881: 書式に合わない行は読まない（否定形） ----

    [Theory]
    [InlineData("利確: AAPL +5% では利確しない")]
    [InlineData("利確: AAPL +5% 以外")]
    [InlineData("利確: AAPL $250 割ったら")]
    [InlineData("利確: AAPL +5%で利確")]
    [InlineData("利確: AAPL +5%。")]
    [InlineData("利確: AAPL +5%; MSFT +8%")]
    [InlineData("利確: AAPL 5%")]
    [InlineData("利確: AAPL -5%")]
    [InlineData("利確: AAPL +0%")]
    [InlineData("利確: AAPL $0")]
    [InlineData("利確: AAPL +5% (0%)")]
    [InlineData("利確: AAPL +5% (101%)")]
    [InlineData("利確: AAPL +5% 50%を利確")]
    [InlineData("利確: AAPL +5% 50%")]
    [InlineData("利確: AAPL +5% (50)")]
    [InlineData("利確: aapl +5%")]
    [InlineData("利確: Apple +5%")]
    [InlineData("利確: アップル +5%")]
    [InlineData("利確: 7203 +5%")]
    [InlineData("利確: AAPL+5%")]
    [InlineData("利確: AAPL")]
    [InlineData("利確:")]
    [InlineData("利確: +5%")]
    [InlineData("利確: AAPL +5% +8%")]
    [InlineData("利確: AAPL +5% $250")]
    [InlineData("利確: AAPL 250")]
    [InlineData("利確: AAPL 250USD")]
    [InlineData("利確: AAPL $1,25")]
    [InlineData("利確: AAPL $1250,000")]
    [InlineData("利確: AAPL +1/2%")]
    [InlineData("利確: 全銘柄,AAPL +5%")]
    [InlineData("利確: AAPL,全銘柄 +5%")]
    [InlineData("利確: AAPL、MSFT +5%")]
    [InlineData("利確: AAPL ABCDEFGHIJK +5%")]
    [InlineData("利確: ABCDEFGHIJK +5%")]
    [InlineData("-- 利確: AAPL +5%")]
    public void 書式に合わない行は読まない(string line)
    {
        PolicyTakeProfitConditions.Parse(line).Should().BeEmpty();
        PolicyTakeProfitConditions.HasAny(line).Should().BeFalse();
    }

    // 「利確:」の見出しで始まらない行は、数値があっても読まない（自由文。「利確」の後ろにコロンがある行は書式外として方針全体を読まない＝T-10-1942）。
    // ［2026-10-02 / #1129 第 4 回監査 R1］コロンの有無を問わず「利確」を含む行は書式外なら方針全体を読まない（T-10-1947〜1949）。
    [Theory]
    [InlineData("利確 AAPL +5%")]
    [InlineData("利確：　")]
    [InlineData("利確目標: AAPL +5%")]
    [InlineData("AAPL 利確: +5%")]
    [InlineData("**利確:** AAPL +5%")]
    [InlineData("> 利確: AAPL +5%")]
    [InlineData("")]
    [InlineData(null)]
    public void 見出しで始まらない行と空は読まない(string? policy)
    {
        PolicyTakeProfitConditions.Parse(policy).Should().BeEmpty();
    }

    // 🔴 書式に合わない「利確:」行が 1 行でもあれば、書式どおりの行があっても方針全体を読まない
    // （読めない行が銘柄の上書きだったときに、全銘柄や他の行を当てて「達した」と書かない）。
    [Theory]
    [InlineData("利確: 全銘柄 +3%\n利確: AAPL +5% では利確しない")]
    [InlineData("利確: AAPL +5% 以外\n利確: 全銘柄 +3%")]
    [InlineData("利確: MSFT +8%\n- 利確: AAPL 5%")]
    public void 書式に合わない利確の行が1行でもあれば方針全体を読まない(string policy)
    {
        PolicyTakeProfitConditions.Parse(policy).Should().BeEmpty();
        PolicyTakeProfitConditions.HasAny(policy).Should().BeFalse();
        PolicyTakeProfitConditions.Reached(
            PolicyTakeProfitConditions.ForSymbol(policy, "AAPL"), true, 100m, 110m, Currency.Usd).Should().BeEmpty();
    }

    // ---- T-10-1942: 行頭の見出し以外の「利確 … :」行も候補にし、書式に合わなければ方針全体を読まない（#1129 第 3 回監査 F1・否定形） ----

    // 🔴 「利確: 全銘柄 +5%」と、AAPL を上書きするつもりの書式外の行。候補を行頭の厳密な見出しだけにすると、書式外の行を黙って飛ばし、
    // 全銘柄 +5% を AAPL に当てて 100→106 を「達した」と書く（監査の実測。15 表記すべて）。
    [Theory]
    [InlineData("利確 : AAPL +20%")]
    [InlineData("**利確:** AAPL +20%")]
    [InlineData("**利確: AAPL +20%**")]
    [InlineData("1. 利確: AAPL +20%")]
    [InlineData("• 利確: AAPL +20%")]
    [InlineData("- - 利確: AAPL +20%")]
    [InlineData("> 利確: AAPL +20%")]
    [InlineData("- [ ] 利確: AAPL +20%")]
    [InlineData("利確（AAPL）: +20%")]
    [InlineData("AAPL 利確: +20%")]
    [InlineData("利確条件: AAPL +20%")]
    [InlineData("\u200B利確: AAPL +20%")]
    [InlineData("\uFEFF利確: AAPL +20%")]
    [InlineData("\u200F利確: AAPL +20%")]
    [InlineData("利\u200B確: AAPL +20%")]
    public void 行頭の見出し以外の利確の行も書式に合わなければ方針全体を読まない(string overrideLine)
    {
        var policy = "利確: 全銘柄 +5%\n" + overrideLine;

        PolicyTakeProfitConditions.Parse(policy).Should().BeEmpty();
        PolicyTakeProfitConditions.HasAny(policy).Should().BeFalse("警告の対象（方針の利確の条件を読めない）");
        PolicyTakeProfitConditions.ForSymbol(policy, "AAPL").Should().BeEmpty();
        PolicyTakeProfitConditions.Reached(
            PolicyTakeProfitConditions.ForSymbol(policy, "AAPL"), true, 100m, 106m, Currency.Usd).Should().BeEmpty();
        // 対照: 上書きの行が無ければ全銘柄 +5% に達している（上の否定は書式外の行だけが崩している）。
        PolicyTakeProfitConditions.Reached(
            PolicyTakeProfitConditions.ForSymbol("利確: 全銘柄 +5%", "AAPL"), true, 100m, 106m, Currency.Usd).Should().ContainSingle();
    }

    // ---- T-10-1943: 書式文字を除くのは候補の判定だけ（銘柄・語の間・行末の書式文字は従来どおり書式外＝方針全体を読まない） ----

    [Theory]
    [InlineData("利確: AA\u200BPL +20%")]
    [InlineData("利確: AAPL\u200B +20%")]
    [InlineData("利確: AAPL \u200B+20%")]
    [InlineData("利確: AAPL +20%\u200B")]
    [InlineData("利確: AAPL +20%\u200F")]
    public void 書式文字を除くのは候補の判定だけで書式の照合は除かない(string overrideLine)
    {
        var policy = "利確: 全銘柄 +5%\n" + overrideLine;

        PolicyTakeProfitConditions.Parse(policy).Should().BeEmpty();
        PolicyTakeProfitConditions.Reached(
            PolicyTakeProfitConditions.ForSymbol(policy, "AAPL"), true, 100m, 106m, Currency.Usd).Should().BeEmpty();
    }

    // ---- T-10-1944: 「利確」の語を含む説明の文があれば、書式どおりの行があっても方針全体を読まない ----
    // ［2026-10-02 / #1129 第 4 回監査 R1］挙動を反転した（旧: コロンの無い説明の文は候補にせず、書式どおりの行を読んで到達した）。
    // コロンを候補の条件にすると、コロンの無い上書き行（「利確 AAPL +20%」等）を黙って飛ばし全銘柄の行を当てるため、語を含む行はすべて候補にする。

    [Theory]
    [InlineData("含み益が出たら半分を利確する。")]
    [InlineData("方針: 押し目で拾い、含み益が出たら利確する")]
    [InlineData("利確の理由は決算前の利益確定である")]
    [InlineData("権利確定日の前日は新規の空売りをしない")]
    public void 利確の語を含む説明の文があれば方針全体を読まない(string prose)
    {
        var policy = prose + "\n利確: AAPL +5% (50%)";

        PolicyTakeProfitConditions.Parse(policy).Should().BeEmpty();
        PolicyTakeProfitConditions.HasAny(policy).Should().BeFalse("警告の対象（方針の利確の条件を読めない）");
        PolicyTakeProfitConditions.Reached(
            PolicyTakeProfitConditions.ForSymbol(policy, "AAPL"), true, 100m, 106m, Currency.Usd).Should().BeEmpty();
        // 対照: 語を含まない説明の文なら書式どおりの行を読み、到達する。
        const string control = "含み益が出たら半分を売る。\n利確: AAPL +5% (50%)";
        PolicyTakeProfitConditions.Reached(
            PolicyTakeProfitConditions.ForSymbol(control, "AAPL"), true, 100m, 106m, Currency.Usd).Should().Equal(Pct("AAPL", 5m, 50m));
    }

    // ---- T-10-1947: 「利確」の後ろに半角コロンが無い上書きの行も、全銘柄の行と並べば方針全体を読まない（#1129 第 4 回監査 R1・否定形） ----

    // 監査の実測: 「- 利確: 全銘柄 +5%」と、AAPL を上書きするつもりのコロンの無い行。候補を「利確 … :」にしていたため、どれも黙って飛ばされ、
    // 全銘柄 +5% を AAPL に当てて 100→106 を「達した」と書いた。
    [Theory]
    [InlineData("- 利確 AAPL +20%")]
    [InlineData("利確 AAPL +20%")]
    [InlineData("利確; AAPL +20%")]
    [InlineData("利確=AAPL +20%")]
    [InlineData("利確 → AAPL +20%")]
    [InlineData("利確 - AAPL +20%")]
    [InlineData("利確｜AAPL +20%")]
    [InlineData("利確(AAPL)+20%")]
    [InlineData("利確（AAPL）＝+20%")]
    [InlineData("利確ライン AAPL +20%")]
    [InlineData("| 利確 | AAPL | +20% |")]
    [InlineData("### 利確\n- AAPL +20%")]
    [InlineData("### 利確条件\n- AAPL: +20%")]
    [InlineData("AAPL 利確 +20%")]
    [InlineData("AAPL: 利確 +20%")]
    [InlineData("ただし AAPL は +20% で利確")]
    [InlineData("利確\r\n: AAPL +20%")]
    // NFKC で半角コロンにならない、コロンに似た字。
    [InlineData("利確\uA789 AAPL +20%")]
    [InlineData("利確\u2236 AAPL +20%")]
    [InlineData("利確\u02F8 AAPL +20%")]
    [InlineData("利確\u02D0 AAPL +20%")]
    [InlineData("利確\u0589 AAPL +20%")]
    [InlineData("利確\u05C3 AAPL +20%")]
    [InlineData("利確\u1804 AAPL +20%")]
    [InlineData("利確\u205A AAPL +20%")]
    [InlineData("利確\u2982 AAPL +20%")]
    [InlineData("利確\u0F0D AAPL +20%")]
    [InlineData("利確\u1361 AAPL +20%")]
    [InlineData("利確\u0703 AAPL +20%")]
    [InlineData("利確\uA4FD AAPL +20%")]
    [InlineData("利確\u16EC AAPL +20%")]
    [InlineData("利確\u0903 AAPL +20%")]
    [InlineData("利確\u05F4 AAPL +20%")]
    [InlineData("利確 \u2014 AAPL +20%")]
    public void コロンの無い利確の上書きの行も方針全体を読まない(string overrideLine) => AssertUnreadableWithAllSymbols(overrideLine);

    // ---- T-10-1948: 候補の判定で空白・結合文字・異体字セレクタ・制御文字・見えない字を除く（#1129 第 4 回監査 Y1・Y2・否定形） ----

    [Theory]
    [InlineData("利 確: AAPL +20%")] // 空白
    [InlineData("利\u3000確 AAPL +20%")] // 全角の空白
    [InlineData("利\U000E0100確: AAPL +20%")] // 異体字セレクタ（IVS。Mn）
    [InlineData("利\uFE00確: AAPL +20%")] // 異体字セレクタ（Mn）
    [InlineData("利\u0301確: AAPL +20%")] // 結合文字（Mn）
    [InlineData("利\u20DD確: AAPL +20%")] // 囲みの結合文字（Me）
    [InlineData("利\u0903確: AAPL +20%")] // 結合文字（Mc）
    [InlineData("利\u0007確: AAPL +20%")] // 制御文字（Cc）
    [InlineData("利\u200B確 AAPL +20%")] // 書式文字（Cf）
    [InlineData("利\uE000確: AAPL +20%")] // 私用（Co）
    [InlineData("利\u0378確: AAPL +20%")] // 未割り当て（Cn）
    [InlineData("利\u3164確: AAPL +20%")] // ハングルの埋め字
    [InlineData("利\u115F確: AAPL +20%")]
    [InlineData("利\u1160確: AAPL +20%")]
    [InlineData("利\uFFA0確: AAPL +20%")]
    [InlineData("利\u2800確: AAPL +20%")] // 点字の空白
    [InlineData("利\uFFFD確: AAPL +20%")] // 置換文字
    [InlineData("利\uFFFC確: AAPL +20%")] // オブジェクト置換文字
    [InlineData("利\U0001D159確: AAPL +20%")] // 楽譜の空の符頭（見えない記号）
    public void 候補の判定で見えない字を除く(string overrideLine) => AssertUnreadableWithAllSymbols(overrideLine);

    // ---- T-10-1949: 「利確」の語の判定は線形で終わる（#1129 第 4 回監査 Y5。旧「利確.*:」は後戻りで 100k 字に約 28 秒） ----

    [Fact]
    public void 利確の語を繰り返す長い入力も線形で終わる()
    {
        var huge = "- 利確: 全銘柄 +5%\n" + string.Concat(Enumerable.Repeat("利確", 50_000));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        PolicyTakeProfitConditions.Parse(huge).Should().BeEmpty();
        sw.Stop();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    private static void AssertUnreadableWithAllSymbols(string overrideLine)
    {
        var policy = "- 利確: 全銘柄 +5%\n" + overrideLine;

        PolicyTakeProfitConditions.Parse(policy).Should().BeEmpty(overrideLine);
        PolicyTakeProfitConditions.HasAny(policy).Should().BeFalse("警告の対象（方針の利確の条件を読めない）");
        PolicyTakeProfitConditions.ForSymbol(policy, "AAPL").Should().BeEmpty();
        PolicyTakeProfitConditions.Reached(
            PolicyTakeProfitConditions.ForSymbol(policy, "AAPL"), true, 100m, 106m, Currency.Usd).Should().BeEmpty();
        // 対照: 上書きの行が無ければ全銘柄 +5% に達している。
        PolicyTakeProfitConditions.Reached(
            PolicyTakeProfitConditions.ForSymbol("- 利確: 全銘柄 +5%", "AAPL"), true, 100m, 106m, Currency.Usd).Should().ContainSingle();
    }

    // ---- T-10-1882: 銘柄への当て方（名指しが全銘柄を上書きする） ----

    [Fact]
    public void 名指しの行が全銘柄の行を上書きし_他の銘柄の行は掛からない()
    {
        const string policy = "利確: 全銘柄 +3%\n利確: AAPL +6%\n利確: MSFT $450";

        PolicyTakeProfitConditions.ForSymbol(policy, "AAPL").Should().Equal(Pct("AAPL", 6m));
        PolicyTakeProfitConditions.ForSymbol(policy, " aapl ").Should().Equal(Pct("AAPL", 6m));
        PolicyTakeProfitConditions.ForSymbol(policy, "NVDA").Should().Equal(Pct(null, 3m));
        PolicyTakeProfitConditions.ForSymbol("利確: MSFT +8%", "AAPL").Should().BeEmpty();

        // 全銘柄 +3% には達しているが、AAPL の行（+6%）には達していない → 到達しない。
        PolicyTakeProfitConditions.Reached(
            PolicyTakeProfitConditions.ForSymbol(policy, "AAPL"), true, 100m, 104m, Currency.Usd).Should().BeEmpty();
        PolicyTakeProfitConditions.Reached(
            PolicyTakeProfitConditions.ForSymbol(policy, "NVDA"), true, 100m, 104m, Currency.Usd).Should().Equal(Pct(null, 3m));
    }

    // ---- T-10-1883: 到達の計算（ロング・ショート・率・価格） ----

    [Theory]
    [InlineData(true, "100", "105", true)]
    [InlineData(true, "100", "104.99", false)]
    [InlineData(false, "100", "95", true)]
    [InlineData(false, "100", "95.01", false)]
    [InlineData(false, "100", "106", false)]
    public void 率はロングで値上がり_ショートで値下がりを含み益として比べる(bool isLong, string entry, string mark, bool reached)
    {
        var e = decimal.Parse(entry, System.Globalization.CultureInfo.InvariantCulture);
        var m = decimal.Parse(mark, System.Globalization.CultureInfo.InvariantCulture);

        PolicyTakeProfitConditions.Reached([Pct("AAPL", 5m)], isLong, e, m, Currency.Usd).Count.Should().Be(reached ? 1 : 0);
    }

    [Theory]
    [InlineData(true, "200", "230", true)]
    [InlineData(true, "200", "229.99", false)]
    [InlineData(false, "260", "230", true)]
    [InlineData(false, "260", "230.01", false)]
    public void 価格はロングで以上_ショートで以下を到達とする(bool isLong, string entry, string mark, bool reached)
    {
        var e = decimal.Parse(entry, System.Globalization.CultureInfo.InvariantCulture);
        var m = decimal.Parse(mark, System.Globalization.CultureInfo.InvariantCulture);

        PolicyTakeProfitConditions.Reached([Price("AAPL", 230m)], isLong, e, m, Currency.Usd).Count.Should().Be(reached ? 1 : 0);
    }

    [Fact]
    public void 取得単価か現在値が正でなければ到達としない()
    {
        PolicyTakeProfitConditions.Reached([Pct(null, 5m)], true, 0m, 110m, Currency.Usd).Should().BeEmpty();
        PolicyTakeProfitConditions.Reached([Pct(null, 5m)], true, 100m, 0m, Currency.Usd).Should().BeEmpty();
        PolicyTakeProfitConditions.Reached([], true, 100m, 110m, Currency.Usd).Should().BeEmpty();
        PolicyTakeProfitConditions.GainPercent(false, 100m, 94m).Should().Be(6m);
    }

    // ---- T-10-1892: 同じ銘柄の行が複数あればすべてに達したときだけ到達（最も控えめ） ----

    [Theory]
    [InlineData("利確: AAPL +5%\n利確: AAPL +8%", true, "100", "106", false)]
    [InlineData("利確: AAPL +5%\n利確: AAPL +8%", true, "100", "108", true)]
    [InlineData("利確: AAPL +8%\n利確: AAPL,MSFT +5%", true, "100", "106", false)]
    [InlineData("利確: 全銘柄 +5%\n利確: 全銘柄 +8%", true, "100", "106", false)]
    [InlineData("利確: 全銘柄 +5%\n利確: 全銘柄 +8%", true, "100", "108", true)]
    [InlineData("利確: AAPL $250\n利確: AAPL $230", true, "200", "240", false)]
    [InlineData("利確: AAPL $250\n利確: AAPL $230", true, "200", "250", true)]
    [InlineData("利確: AAPL $150\n利確: AAPL $170", false, "200", "160", false)]
    [InlineData("利確: AAPL $150\n利確: AAPL $170", false, "200", "150", true)]
    [InlineData("利確: AAPL +5%\n利確: AAPL $250", true, "200", "211", false)]
    [InlineData("利確: AAPL +5%\n利確: AAPL $250", true, "200", "250", true)]
    public void 同じ銘柄の行が複数あればすべてに達したときだけ到達とする(
        string policy, bool isLong, string entry, string mark, bool reached)
    {
        var e = decimal.Parse(entry, System.Globalization.CultureInfo.InvariantCulture);
        var m = decimal.Parse(mark, System.Globalization.CultureInfo.InvariantCulture);
        var conditions = PolicyTakeProfitConditions.ForSymbol(policy, "AAPL");

        conditions.Should().HaveCount(2);
        var result = PolicyTakeProfitConditions.Reached(conditions, isLong, e, m, Currency.Usd);
        if (reached)
            result.Should().Equal(conditions);
        else
            result.Should().BeEmpty();
    }

    // ---- T-10-1893: 損の側の価格・含み益 0 以下・通貨違いは到達としない（否定形） ----

    [Theory]
    [InlineData(false, "200", "190")] // ショートで取得単価より上の 230 は損の側（現在値 ≤ 230 でも到達しない）
    [InlineData(false, "200", "220")]
    [InlineData(true, "250", "260")] // ロングで取得単価より下の 230 は損の側（現在値 ≥ 230 でも到達しない）
    [InlineData(true, "230", "240")] // 取得単価と同じ価格も利益の側ではない
    public void 損の側の価格は到達としない(bool isLong, string entry, string mark)
    {
        var e = decimal.Parse(entry, System.Globalization.CultureInfo.InvariantCulture);
        var m = decimal.Parse(mark, System.Globalization.CultureInfo.InvariantCulture);

        PolicyTakeProfitConditions.Reached([Price("AAPL", 230m)], isLong, e, m, Currency.Usd).Should().BeEmpty();
    }

    [Fact]
    public void 含み益が0以下なら直接渡した条件でも到達としない()
    {
        // 書式はしきい値 0 以下を読まないが、部品を直接呼ぶ経路の多重の防御を固定する。
        PolicyTakeProfitConditions.Reached([Pct(null, -5m)], true, 100m, 97m, Currency.Usd).Should().BeEmpty();
        PolicyTakeProfitConditions.Reached([Pct(null, 0m)], true, 100m, 100m, Currency.Usd).Should().BeEmpty();
        PolicyTakeProfitConditions.Reached([Pct(null, -5m)], false, 100m, 103m, Currency.Usd).Should().BeEmpty();
    }

    [Fact]
    public void 価格の通貨が市場の通貨と違えば到達としない()
    {
        var yen = PolicyTakeProfitConditions.ForSymbol("利確: AAPL 230円", "AAPL");
        PolicyTakeProfitConditions.Reached(yen, true, 200m, 231m, Currency.Usd).Should().BeEmpty();
        PolicyTakeProfitConditions.Reached(yen, true, 200m, 231m, Currency.Jpy).Should().Equal(yen);

        var usd = PolicyTakeProfitConditions.ForSymbol("利確: AAPL $230", "AAPL");
        PolicyTakeProfitConditions.Reached(usd, true, 200m, 231m, Currency.Jpy).Should().BeEmpty();
    }

    // ---- T-10-1894: 2 度の監査の場面（自由文）は、どれも条件にならず到達もしない（否定形） ----

    private static readonly (string Policy, string Symbol, bool IsLong, decimal Entry, decimal Mark)[] AuditProbeCases =
    [
        ("AAPLは+5%で利確、MSFTは+8%で利確する。", "MSFT", true, 100m, 106m),
        ("AAPLは+5%で利確、MSFTは+8%で利確する。", "AAPL", true, 100m, 106m),
        ("NVDAは+3%、AMZNは+5%で利確", "NVDA", true, 100m, 104m),
        ("NVDAは+3%、AMZNは+5%で利確", "AMZN", true, 100m, 104m),
        ("NVDAは+3%、AMZNは+5%で利確", "AMZN", true, 100m, 106m),
        ("全銘柄+5%で利確。ただしMSFTは+8%", "MSFT", true, 100m, 106m),
        ("全銘柄+5%で利確。ただしMSFTは+8%で利確", "MSFT", true, 100m, 106m),
        ("全銘柄+5%で利確、ただしMSFTは+8%", "MSFT", true, 100m, 106m),
        ("全銘柄は+5%で利確、ただしMSFTは+8%で利確", "MSFT", true, 100m, 106m),
        ("+5%か$250で利確", "AAPL", true, 200m, 251m),
        ("+5%か$250で利確", "AAPL", true, 240m, 251m),
        ("250ドル以上で利確", "AAPL", true, 200m, 251m),
        ("250ドル以上で利確", "AAPL", false, 300m, 251m),
        ("損切り-2%、利確+4%", "AAPL", true, 100m, 102.5m),
        ("損切り-2%、利確+4%", "AAPL", true, 100m, 104m),
        ("損切り-2%で利確+4%", "AAPL", true, 100m, 102.5m),
        ("利確+4%、損切り2%", "AAPL", true, 100m, 103m),
        ("利確は+4%、損切りは-2%", "AAPL", true, 100m, 102.5m),
        ("前日比+3%で利確", "AAPL", true, 100m, 103m),
        ("含み益3%で半分、5%で残り", "AAPL", true, 100m, 104m),
        ("含み益3%で半分、5%で残りを利確", "AAPL", true, 100m, 104m),
        ("平均取得単価から+5%で利確", "AAPL", true, 100m, 106m),
        ("ポジションの50%を+5%で利確", "AAPL", true, 100m, 106m),
        ("ポジションの50%を+5%で利確", "AAPL", true, 100m, 10.1m),
        ("ＮＶＤＡは＋３％で利確、ＡＭＺＮは＋８％で利確", "AMZN", true, 100m, 104m),
        ("Take profit at +5% for AAPL, 利確", "AAPL", true, 100m, 106m),
        ("AAPL take profit +5%で利確、MSFT +8%", "MSFT", true, 100m, 106m),
        ("+5%で50%利確", "AAPL", true, 100m, 51m),
        ("+5%で50%利確", "AAPL", true, 100m, 106m),
        ("50%を+5%で利確", "AAPL", true, 100m, 51m),
        ("口座の10%まで買い、+5%で利確", "AAPL", true, 100m, 11m),
        ("1銘柄あたり口座の10%まで、含み益が十分に出たら利確", "AAPL", true, 100m, 150m),
        ("アップルは+5%で利確", "AAPL", true, 100m, 106m),
        ("マイクロソフト(MSFT)は+5%で利確", "AAPL", true, 100m, 106m),
        ("トヨタ(7203)は+5%で利確", "AAPL", true, 100m, 106m),
        ("7203は+5%で利確", "AAPL", true, 100m, 106m),
        ("AAPLは5ドル上昇したら利確", "AAPL", true, 100m, 106m),
        ("AAPLは利益が500ドルで利確", "AAPL", true, 100m, 600m),
        ("AAPLは+5ドルで利確", "AAPL", true, 100m, 106m),
        ("AAPLは5ドル高で利確", "AAPL", true, 100m, 106m),
        ("AAPLは5ドルで利確", "AAPL", true, 1m, 6m),
        ("AAPLは5ドル値幅で利確", "AAPL", true, 1m, 6m),
        ("AAPLは5ドル分で利確", "AAPL", true, 1m, 6m),
        ("AAPLは5ドルの利益で利確", "AAPL", true, 1m, 6m),
        ("AAPLは5ドル伸びたら利確", "AAPL", true, 1m, 6m),
        ("AAPLは5ドル以上の含み益で利確", "AAPL", true, 1m, 6m),
        ("AAPLは1株あたり5ドル稼げたら利確", "AAPL", true, 1m, 6m),
        ("AAPLは500ドル儲かったら利確", "AAPL", true, 1m, 600m),
        ("AAPLは500ドルのプラスで利確", "AAPL", true, 1m, 600m),
        ("AAPLは500ドル取れたら利確", "AAPL", true, 1m, 600m),
        ("AAPLは合計500ドルで利確", "AAPL", true, 1m, 600m),
        ("AAPLは評価益500ドルで利確", "AAPL", true, 1m, 600m),
        ("AAPLは5%上昇で利確", "AAPL", true, 100m, 106m),
        ("+3%で利確、ただし10:30以降", "AAPL", true, 100m, 104m),
        ("2日で3%動いたら利確", "AAPL", true, 100m, 104m),
        ("3日以内に+5%なら利確", "AAPL", true, 100m, 106m),
        ("VIXが20%超なら利確", "AAPL", true, 100m, 121m),
        ("出来高が前日の150%で利確", "AAPL", true, 100m, 151m),
        ("SPYが+2%で利確", "AAPL", true, 100m, 103m),
        ("QQQが+2%上昇したらAAPLを利確", "AAPL", true, 100m, 103m),
        ("ナスダック100が+2%ならAAPL利確", "AAPL", true, 100m, 103m),
        ("TSLAは+10%で利確、ただしAAPLは対象外", "AAPL", true, 100m, 111m),
        ("AAPLとMSFTは+5%で利確", "MSFT", true, 100m, 106m),
        ("AAPL・MSFTは+5%で利確", "MSFT", true, 100m, 106m),
        ("AAPL/MSFTは+5%で利確", "MSFT", true, 100m, 106m),
        ("AAPL +5%、MSFT +8%で利確", "MSFT", true, 100m, 106m),
        ("AAPL +5% MSFT +8% で利確", "MSFT", true, 100m, 106m),
        ("AAPL:+5%/MSFT:+8%で利確", "MSFT", true, 100m, 106m),
        ("AAPL(+5%)とMSFT(+8%)を利確", "MSFT", true, 100m, 106m),
        ("利確: AAPL +5%; MSFT +8%", "MSFT", true, 100m, 106m),
        ("利確 AAPL+5% MSFT+8%", "MSFT", true, 100m, 106m),
        ("利確目標 AAPL 5%・MSFT 8%", "MSFT", true, 100m, 106m),
        ("AAPLを+5%で利確\nMSFTも同様", "MSFT", true, 100m, 106m),
        ("+5%で利確（AAPL除く）", "AAPL", true, 100m, 106m),
        ("AAPL以外は+5%で利確", "AAPL", true, 100m, 106m),
        ("+5%で利確。AAPLは除外", "AAPL", true, 100m, 106m),
        ("AAPLは利確しない、MSFTは+5%で利確", "AAPL", true, 100m, 106m),
        ("AAPLは利確せず保有継続。+5%で利確は他銘柄", "AAPL", true, 100m, 106m),
        ("+5%では利確しない", "AAPL", true, 100m, 106m),
        ("+5%未満では利確しない", "AAPL", true, 100m, 106m),
        ("+5%まで利確しない、+10%で利確", "AAPL", true, 100m, 106m),
        ("+20%を超えるまで利確しない", "AAPL", true, 100m, 121m),
        ("ショートは-5%で利確", "AAPL", false, 100m, 96m),
        ("ショートは5%下がったら利確", "AAPL", false, 100m, 96m),
        ("ショートは95ドルで利確", "AAPL", false, 100m, 94m),
        ("ショートは105ドルで利確", "AAPL", false, 100m, 94m),
        ("AAPLは230ドルで利確", "AAPL", false, 200m, 190m),
        ("AAPLは230ドルで利確", "AAPL", true, 200m, 231m),
        ("AAPLは230円で利確", "AAPL", true, 200m, 231m),
        ("AAPLは2万円で利確", "AAPL", true, 200m, 231m),
        ("AAPLは1,250ドルで利確", "AAPL", true, 200m, 300m),
        ("AAPLは$1,250で利確", "AAPL", true, 200m, 300m),
        ("AAPLは1250.5ドルで利確", "AAPL", true, 200m, 1300m),
        ("AAPLは50%の利益で利確", "AAPL", true, 100m, 151m),
        ("AAPLは目標株価250ドルで利確", "AAPL", true, 200m, 251m),
        ("AAPLは250ドル付近で利確", "AAPL", true, 200m, 251m),
        ("AAPLは250ドルを下回ったら利確", "AAPL", true, 200m, 251m),
        ("AAPLは250ドル割れで利確", "AAPL", true, 200m, 251m),
        ("AAPLは250ドルを割ったら利確", "AAPL", true, 200m, 260m),
        ("AAPLは250ドルまで戻ったら利確", "AAPL", true, 200m, 260m),
        ("+5%から3%反落で利確", "AAPL", true, 100m, 106m),
        ("ピークから3%下げたら利確", "AAPL", true, 100m, 104m),
        ("高値から3%下げたら利確", "AAPL", true, 100m, 104m),
        ("最高値から3%戻したら利確", "AAPL", true, 100m, 104m),
        ("トレーリング3%で利確", "AAPL", true, 100m, 104m),
        ("+10%到達後に3%押したら利確", "AAPL", true, 100m, 104m),
        ("含み益の30%を失ったら利確", "AAPL", true, 100m, 131m),
        ("リスクリワード1:2、2%で利確", "AAPL", true, 100m, 103m),
        ("損切りの2倍、つまり4%で利確", "AAPL", true, 100m, 105m),
        ("AAPLの+5%で利確", "MSFT", true, 100m, 106m),
        ("Appleは+5%で利確", "AAPL", true, 100m, 106m),
        ("apple: +5%で利確", "AAPL", true, 100m, 106m),
        ("BRK.Bは+5%で利確、AAPLは+8%で利確", "AAPL", true, 100m, 106m),
        ("AAPLは+5%で利確 MSFTは+8%で利確", "MSFT", true, 100m, 106m),
        ("AAPLは+5%で利確 / MSFTは+8%で利確", "MSFT", true, 100m, 106m),
        ("AAPLは+5%で利確と MSFTは+8%で利確", "MSFT", true, 100m, 106m),
        ("AAPLは+5%で利確・MSFTは+8%で利確", "MSFT", true, 100m, 106m),
        ("AAPLは+5%で利確 and MSFTは+8%で利確", "MSFT", true, 100m, 106m),
        ("AAPLは+5%で利確、MSFTは+8%", "MSFT", true, 100m, 106m),
        ("AAPLは+5%で利確; MSFTは+8%", "MSFT", true, 100m, 106m),
        ("AAPLは+5%で利確 (MSFTは+8%)", "MSFT", true, 100m, 106m),
        ("AAPLは+5%で利確（MSFTは+8%）", "MSFT", true, 100m, 106m),
        ("AAPL, MSFTは+5%で利確", "MSFT", true, 100m, 106m),
        ("AAPLは+5%、で利確", "AAPL", true, 100m, 106m),
        ("目標: +5%で利確", "AAPL", true, 100m, 106m),
        ("利確ライン +5%", "AAPL", true, 100m, 106m),
        ("利確ライン: 230ドル", "AAPL", true, 200m, 231m),
        ("利確 5%", "AAPL", true, 100m, 106m),
        ("+5% take profit 利確", "AAPL", true, 100m, 106m),
        ("+5％で利確", "AAPL", true, 100m, 106m),
        ("＋５．５％で利確", "AAPL", true, 100m, 106m),
        ("５ドル上昇で利確", "AAPL", true, 1m, 6m),
        ("＄２５０で利確", "AAPL", true, 200m, 251m),
        ("250USDで利確", "AAPL", true, 200m, 251m),
        ("USD250で利確", "AAPL", true, 200m, 251m),
        ("$250-$260で利確", "AAPL", true, 200m, 251m),
        ("250〜260ドルで利確", "AAPL", true, 200m, 251m),
        ("+5〜+8%で利確", "AAPL", true, 100m, 106m),
        ("5-8%で利確", "AAPL", true, 100m, 106m),
        ("5%-8%で利確", "AAPL", true, 100m, 106m),
        ("税引き後5%で利確", "AAPL", true, 100m, 105.5m),
        ("手数料込みで+5%で利確", "AAPL", true, 100m, 105m),
        ("年率20%で利確", "AAPL", true, 100m, 121m),
        ("確率70%で利確", "AAPL", true, 100m, 71m),
        ("勝率60%なら利確", "AAPL", true, 100m, 61m),
        ("2026年は+5%で利確", "AAPL", true, 100m, 106m),
        ("+5%で利確、残り50%は保有", "AAPL", true, 100m, 51m),
        ("+5%で利確、ただし50%は残す", "AAPL", true, 100m, 51m),
        ("+5%で利確（保有の50%）", "AAPL", true, 100m, 51m),
        ("50%は+5%で利確", "AAPL", true, 100m, 51m),
        ("半分は+5%、残りは+10%で利確", "AAPL", true, 100m, 106m),
        ("3分の1を+5%で利確", "AAPL", true, 100m, 106m),
        ("1/3を+5%で利確", "AAPL", true, 100m, 106m),
        ("+5%で1/2を利確", "AAPL", true, 100m, 106m),
        ("MSFTは評価益500ドルで利確", "MSFT", true, 450m, 501m),
        ("MSFTは500ドル儲かったら利確", "MSFT", true, 450m, 501m),
        ("MSFTは1,450ドルで利確", "MSFT", true, 430m, 455m),
        ("AAPLは250ドルを下回ったら利確", "AAPL", true, 200m, 260m),
        ("トレーリングストップ3%で利確", "AAPL", true, 100m, 104m),
        ("+5%未満では利確しない", "AAPL", true, 100m, 104m),
        ("基本は+5%で利確、MSFTは+8%で利確", "MSFT", true, 100m, 106m),
        ("+5%で利確、MSFTは+8%", "MSFT", true, 100m, 106m),
        ("AAPLは+5%、MSFTは+8%で利確", "MSFT", true, 100m, 106m),
        ("AAPLは+5%で、MSFTは+8%で利確", "MSFT", true, 100m, 106m),
        ("AAPL→+5%で利確 MSFT→+8%で利確", "MSFT", true, 100m, 106m),
        ("- AAPL: +5%で利確 - MSFT: +8%で利確", "MSFT", true, 100m, 106m),
        ("apple: +5%で利確", "MSFT", true, 100m, 106m),
        ("トヨタ株は+5%で利確", "AAPL", true, 100m, 106m),
        ("エヌビディアの利確は+5%", "AAPL", true, 100m, 106m),
        ("Microsoft Corp: +5%で利確", "AAPL", true, 100m, 106m),
        ("+5%で利確、残り50%は保有", "AAPL", true, 100m, 151m),
        ("50%は+5%で利確", "AAPL", true, 100m, 151m),
        ("エントリー価格から+5%で利確", "AAPL", true, 100m, 106m),
        ("平均建値から+5%で利確", "AAPL", true, 100m, 106m),
        ("建値から+5%で利確", "AAPL", true, 100m, 106m),
        ("買値から+5%で利確", "AAPL", true, 100m, 106m),
        ("平均取得価格から+5%で利確", "AAPL", true, 100m, 106m),
        ("取得単価比+5%で利確", "AAPL", true, 100m, 106m),
        ("株価が+5%上昇したら利確", "AAPL", true, 100m, 106m),
        ("含み益が+5%に達したら利確", "AAPL", true, 100m, 106m),
        ("利益率+5%で利確", "AAPL", true, 100m, 106m),
        ("目標リターン+5%で利確", "AAPL", true, 100m, 106m),
        ("AAPLは230ドル以上で利確", "AAPL", true, 200m, 231m),
        ("AAPLの利確目標は230ドル", "AAPL", true, 200m, 231m),
        ("AAPLは230ドルに上がったら利確", "AAPL", true, 200m, 231m),
        ("AAPLは230ドルまで上昇したら利確", "AAPL", true, 200m, 231m),
        ("AAPLは株価230ドルで利確", "AAPL", true, 200m, 231m),
        ("保有株は+5%で利確", "AAPL", true, 100m, 106m),
        ("+5%でポジションの半分を利確", "AAPL", true, 100m, 106m),
        ("+3%で保有の1/3、+6%で残りを利確", "AAPL", true, 100m, 104m),
        ("+3%で33%利確、+6%で残り利確", "AAPL", true, 100m, 104m),
        ("+3%で33%を利確、+6%で全量利確", "AAPL", true, 100m, 104m),
        ("+5%で利確する。新規エントリーは見送る", "AAPL", true, 100m, 106m),
        ("新規建ては控え、保有分は+5%で利確", "AAPL", true, 100m, 106m),
        ("新規建ては控え保有分は+5%で利確", "AAPL", true, 100m, 106m),
        ("損切りは-3%、利確は+5%", "AAPL", true, 100m, 106m),
        ("損切り-3%・利確+5%", "AAPL", true, 100m, 106m),
        ("損切り-3% 利確+5%", "AAPL", true, 100m, 106m),
        ("利確+5%（損切り-3%）", "AAPL", true, 100m, 106m),
        ("TP +5% / SL -3% で利確", "AAPL", true, 100m, 106m),
    ];

    public static TheoryData<int> AuditProbeIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < AuditProbeCases.Length; i++)
            data.Add(i);
        return data;
    }

    [Theory]
    [MemberData(nameof(AuditProbeIndexes))]
    public void 監査の場面の自由文は条件にならず到達もしない(int index)
    {
        var (policy, symbol, isLong, entry, mark) = AuditProbeCases[index];

        PolicyTakeProfitConditions.Parse(policy).Should().BeEmpty(policy);
        PolicyTakeProfitConditions.HasAny(policy).Should().BeFalse(policy);
        PolicyTakeProfitConditions.Reached(
            PolicyTakeProfitConditions.ForSymbol(policy, symbol), isLong, entry, mark, Currency.Usd).Should().BeEmpty(policy);
    }

    [Fact]
    public void 監査の場面は2つの監査の表をすべて含む()
    {
        AuditProbeCases.Should().HaveCount(200);
        AuditProbeCases.Select(c => c.Policy).Should().Contain(["AAPLは+5%で利確、MSFTは+8%で利確する。", "TP +5% / SL -3% で利確", "利確: AAPL +5%; MSFT +8%"]);
    }

    // ---- T-10-1895: 表示 ----

    [Fact]
    public void 条件の表示()
    {
        PolicyTakeProfitConditions.Describe(Pct("AAPL", 5m, 50m)).Should().Be("取得単価から +5% で利確（一部利確 50%）");
        PolicyTakeProfitConditions.Describe(Price("AAPL", 230.5m)).Should().Be("価格 230.5 USD で利確");
        PolicyTakeProfitConditions.Describe(Price(null, 3000m, Currency.Jpy)).Should().Be("価格 3000 JPY で利確");
    }

    // ---- T-10-1896: 桁区切りと全角 ----

    [Theory]
    [InlineData("利確: AAPL $1,250", "1250")]
    [InlineData("利確: AAPL 1,250.5 ドル", "1250.5")]
    [InlineData("利確: AAPL $1,234,567", "1234567")]
    [InlineData("利確：ＡＡＰＬ　＄１，２５０", "1250")]
    [InlineData("利確：ＡＡＰＬ　１，２５０ドル（５０％）", "1250")]
    public void 桁区切りのカンマを除き全角を半角へ寄せて読む(string line, string price)
    {
        var expected = decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture);

        var c = PolicyTakeProfitConditions.Parse(line).Should().ContainSingle().Which;
        (c.Symbol, c.Kind, c.Threshold, c.PriceCurrency).Should().Be(("AAPL", TakeProfitThresholdKind.Price, expected, (Currency?)Currency.Usd));
        PolicyTakeProfitConditions.Reached([c], true, 1000m, expected, Currency.Usd).Should().ContainSingle();
    }

    [Fact]
    public void 全角の率と割合を読む()
    {
        PolicyTakeProfitConditions.Parse("・利確：ＭＳＦＴ　＋５．５％（５０％）")
            .Should().Equal(Pct("MSFT", 5.5m, 50m));
        PolicyTakeProfitConditions.Parse("利確：全銘柄　＋８％").Should().Equal(Pct(null, 8m));
    }
}
