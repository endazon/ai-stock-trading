extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-04, ADR-0003, #1129, IADR-0470 決定 3（オーナー裁定 2026-10-01）: 判断は LLM が方針を見て行うまま、方針の決まった書式の「利確:」行
// （IADR-0470 の 2026-10-01 追記 / #1129 再監査。自由文は読まない）の条件にすべて達しているときだけ、保有状況の節で「方針の利確条件に達している」と
// 明示する。数値（含み益の率・条件との比較）はコードで計算する。既定の Hold の規則（ExitFollowsPolicyRule）は変えない。自動の利確は採らない。
// T-10-1889〜1891・T-10-1897〜1899・T-10-1940〜1941・T-10-1945。
public class TakeProfitReachedInPromptTests
{
    private static readonly SizingContext Context =
        new(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

    private static readonly DecisionTrigger Aapl = DecisionTrigger.Scheduled("AAPL", Market.UnitedStates);

    private static DailyPolicy Policy(string text) => new(new DateOnly(2026, 10, 1), text);

    private static string MainPrompt(
        string policy, HeldPosition? held, decimal? price, IReadOnlyList<RejectionReason>? addOnBlockers = null) =>
        TradeDecisionPromptBuilder.Build(Aapl, Policy(policy), Context, currentPrice: price, held: held,
            working: WorkingEntryOrders.None, watchlist: [], addOnBlockers: addOnBlockers);

    private static string Screening(
        string policy, HeldPosition? held, decimal? price, IReadOnlyList<RejectionReason>? addOnBlockers = null) =>
        TradeDecisionPromptBuilder.BuildScreening(Aapl, Policy(policy), Context, currentPrice: price, held: held,
            working: WorkingEntryOrders.None, watchlist: [], addOnBlockers: addOnBlockers);

    private static string[] Lines(string prompt) => prompt.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    private static string ReachedLine(string prompt) =>
        Lines(prompt).Should().ContainSingle(l => l.Contains(TradeDecisionPromptBuilder.TakeProfitReachedLinePrefix)).Which;

    // T-10-1889: ロング 100→106（+6%）で方針の「利確: AAPL +5% (50%)」に達している。本判断・一次の両方の保有状況の節に、条件と系が計算した率を書く。
    [Fact]
    public void 方針の利確条件に達していれば本判断と一次に明示する()
    {
        // #1129 第 4 回監査 R1: 説明の文に「利確」の語があると方針全体が読まれないため、説明の文は「売る」で書く。
        const string policy = "AAPL は押し目で拾い、含み益が出たら半分を売る。\n利確: AAPL +5% (50%)";
        var held = new HeldPosition(10, 100m, 98m);

        foreach (var prompt in new[] { MainPrompt(policy, held, 106m), Screening(policy, held, 106m) })
        {
            ReachedLine(prompt).Should().Contain("取得単価から +5% で利確（一部利確 50%）")
                .And.Contain("+6.00%")
                .And.Contain("平均取得単価 100・現在値 106")
                .And.Contain("手仕舞い（Sell）")
                .And.Contain("全量");
        }

        // 保有状況の節の中にある（リスク制約の節より前）。
        var main = MainPrompt(policy, held, 106m);
        main.IndexOf(TradeDecisionPromptBuilder.TakeProfitReachedLinePrefix, StringComparison.Ordinal)
            .Should().BeGreaterThan(main.IndexOf(TradeDecisionPromptBuilder.HeldPositionSectionTitle, StringComparison.Ordinal))
            .And.BeLessThan(main.IndexOf("# リスク制約", StringComparison.Ordinal));
    }

    // T-10-1889: ショートは値下がりが含み益（100→94 で +6%）。価格の条件はショートで取得単価より下の価格。手仕舞いは Buy。
    [Theory]
    [InlineData("利確: 全銘柄 +5%")]
    [InlineData("利確: AAPL $95")]
    public void ショートは値下がりを含み益として比べる(string policy)
    {
        var prompt = MainPrompt(policy, new HeldPosition(-10, 100m, 102m), 94m);

        ReachedLine(prompt).Should().Contain("+6.00%").And.Contain("手仕舞い（Buy）");
    }

    // T-10-1890（否定形）: 達していない・読める「利確:」行が無い・他の銘柄の行・取得単価や現在値が不明・保有なしでは何も書かない
    // （プロンプトは方針の文以外、到達の行を持たない構成〔develop〕と一字一句同じ）。既定の Hold の規則は変えない。
    [Theory]
    [InlineData("利確: AAPL +5%", 10, "100", "104.99")]
    [InlineData("利確: AAPL +5%\n利確: AAPL +8%", 10, "100", "106")]
    [InlineData("利確: 全銘柄 +3%\n利確: AAPL +8%", 10, "100", "106")]
    [InlineData("利確: AAPL $250", -10, "200", "190")]
    [InlineData("利確: AAPL 230円", 10, "200", "231")]
    [InlineData("含み益が十分に出た段階で利確する。", 10, "100", "150")]
    [InlineData("AAPLは+5%で利確。", 10, "100", "150")]
    [InlineData("利確: MSFT +5%", 10, "100", "150")]
    [InlineData("利確: AAPL +5%", 10, null, "150")]
    [InlineData("利確: AAPL +5%", 10, "100", null)]
    [InlineData("利確: AAPL +5%", 0, "100", "150")]
    public void 達していないか確かめられなければ何も書かない(string policy, int qty, string? entry, string? price)
    {
        var e = entry is null ? (decimal?)null : decimal.Parse(entry, System.Globalization.CultureInfo.InvariantCulture);
        var p = price is null ? (decimal?)null : decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture);
        var held = new HeldPosition(qty, qty == 0 ? null : e, null);

        foreach (var blockers in new IReadOnlyList<RejectionReason>?[] { null, [RejectionReason.MaxPositionsExceeded] })
        {
            var prompt = MainPrompt(policy, held, p, blockers);
            var screening = Screening(policy, held, p, blockers);

            prompt.Should().NotContain(TradeDecisionPromptBuilder.TakeProfitReachedLinePrefix);
            screening.Should().NotContain(TradeDecisionPromptBuilder.TakeProfitReachedLinePrefix);
            // 方針の文以外は、「利確:」行の無い方針と一字一句同じ。
            prompt.Replace(policy, "X", StringComparison.Ordinal).Should().Be(MainPrompt("X", held, p, blockers));
            screening.Replace(policy, "X", StringComparison.Ordinal).Should().Be(Screening("X", held, p, blockers));
            if (qty != 0)
                prompt.Should().Contain(TradeDecisionPromptBuilder.ExitFollowsPolicyRule);
        }
    }

    // T-10-1891: 行の最悪長（書式の最大桁のしきい値と割合を 3 件並べ「ほか」・桁の多い値・通貨表記・一部利確の注記）が一次の縮退の保護分の予約に収まる。
    [Fact]
    public void 行の最悪長は縮退の予約に収まる()
    {
        var policy = string.Join('\n', Enumerable.Repeat("利確: AAPL +999,999,999,999.9999% (99.9999%)", 5));
        // ロングの取得単価を最小・現在値を最大の桁にして、含み益の率（+N%）も最長にする（ショートの率は 100% を超えない）。
        var held = new HeldPosition(1_000_000, 0.0001m, null);
        var line = TradeDecisionPromptBuilder.TakeProfitReachedLine(policy, "AAPL", held, 999_999_999_999.9999m, " JPY", Currency.Jpy);

        line.Should().NotBeNull();
        line.Should().Contain(" ほか 2 件");
        ("- " + line + "\n").Length.Should().BeLessThanOrEqualTo(ScreeningContextAssembler.TakeProfitReachedReserveChars);
    }

    private static string HeldSection(string prompt)
    {
        var start = prompt.IndexOf(TradeDecisionPromptBuilder.HeldPositionSectionTitle, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = prompt.IndexOf("\n# ", start, StringComparison.Ordinal);
        return (end < 0 ? prompt[start..] : prompt[start..(end + 1)]).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    // T-10-1897（#1129 監査 F6）: ショートの到達の行そのものが手仕舞いを Buy と書く（プロンプトの他の行の「手仕舞い（Buy）」で代用しない）。
    [Fact]
    public void ショートの到達の行は手仕舞いをBuyと書く()
    {
        var held = new HeldPosition(-10, 100m, 102m);

        foreach (var prompt in new[] { MainPrompt("利確: AAPL +5%", held, 94m), Screening("利確: AAPL +5%", held, 94m) })
            ReachedLine(prompt).Should().Contain("手仕舞い（Buy）").And.NotContain("Sell");
    }

    // T-10-1898（#1129 監査 F6）: 一次（短縮版）のショートの保有状況の節は、到達していなければ「利確:」行の無い方針と一字一句同じ（全文で固定）。
    [Fact]
    public void 一次のショートの保有状況の節は未到達なら全文が変わらない()
    {
        var held = new HeldPosition(-10, 100m, 102m);

        var section = HeldSection(Screening("利確: AAPL +5%", held, 104m));

        section.Should().Be(
            "# 保有状況（この銘柄）\n"
            + "- 保有: ショート 10 株 / 平均取得単価: 100 / 含み損益率: -4.00% / 記録上の損切りライン: 102（現在値は損切りラインに達しています）\n"
            + "- 保有中の銘柄は、買い増し・売り増しに加えて、手仕舞いの検討に値する場合も本判断へ進めます。"
            + "現在値が記録上の損切りラインに達している建玉は手仕舞いの候補です。（この建玉の手仕舞いは Buy）\n"
            + "\n");
    }

    // T-10-1899（#1129 監査・再監査・否定形）: 監査の場面（自由文・書式に合わない「利確:」行）はプロンプトにも到達の行を出さない。
    [Theory]
    [InlineData("AAPLは+5%で利確、MSFTは+8%で利確する。", "MSFT", 10, "100", "106")]
    [InlineData("AAPL は 230 ドルで利確。", "AAPL", 10, "200", "231")]
    [InlineData("+5%では利確しない", "AAPL", 10, "100", "106")]
    [InlineData("全銘柄+5%で利確。ただしMSFTは+8%", "MSFT", 10, "100", "106")]
    [InlineData("利確: AAPL +5% では利確しない", "AAPL", 10, "100", "106")]
    [InlineData("利確: AAPL +5% 以外", "AAPL", 10, "100", "106")]
    [InlineData("利確: 全銘柄 +3%\n利確: AAPL +8% 以外", "AAPL", 10, "100", "106")]
    [InlineData("利確: AAPL $250 割ったら", "AAPL", 10, "200", "260")]
    [InlineData("利確: AAPL +5%; MSFT +8%", "MSFT", 10, "100", "106")]
    [InlineData("利確: 全銘柄 +5%\n利確 AAPL +20%", "AAPL", 10, "100", "106")] // #1129 第 4 回監査 R1: コロンの無い上書きの行
    [InlineData("利確: 全銘柄 +5%\n| 利確 | AAPL | +20% |", "AAPL", 10, "100", "106")]
    public void 監査の場面では到達の行を出さない(string policy, string symbol, int qty, string entry, string mark)
    {
        var e = decimal.Parse(entry, System.Globalization.CultureInfo.InvariantCulture);
        var m = decimal.Parse(mark, System.Globalization.CultureInfo.InvariantCulture);
        var held = new HeldPosition(qty, e, null);

        TradeDecisionPromptBuilder.TakeProfitReachedLine(policy, symbol, held, m, string.Empty, Currency.Usd).Should().BeNull();
        var trigger = DecisionTrigger.Scheduled(symbol, Market.UnitedStates);
        TradeDecisionPromptBuilder.Build(trigger, Policy(policy), Context, currentPrice: m, held: held,
                working: WorkingEntryOrders.None, watchlist: [])
            .Should().NotContain(TradeDecisionPromptBuilder.TakeProfitReachedLinePrefix);
        TradeDecisionPromptBuilder.BuildScreening(trigger, Policy(policy), Context, currentPrice: m, held: held,
                working: WorkingEntryOrders.None, watchlist: [])
            .Should().NotContain(TradeDecisionPromptBuilder.TakeProfitReachedLinePrefix);
    }

    // T-10-1940（#1129 再監査 Y4）: 本判断で、買い増しが審査で塞がっていても（#1142）利確条件への到達の行は消えない。
    // 塞がりの行 → 出口の既定の規則 → 到達の行の順に並ぶ。
    [Theory]
    [InlineData(10, "106", "Sell")]
    [InlineData(-10, "94", "Buy")]
    public void 本判断は買い増しが塞がっていても到達の行を出す(int qty, string mark, string close)
    {
        var held = new HeldPosition(qty, 100m, null);
        var m = decimal.Parse(mark, System.Globalization.CultureInfo.InvariantCulture);
        var prompt = MainPrompt("利確: 全銘柄 +5%", held, m, [RejectionReason.MaxPositionsExceeded]);
        var lines = Lines(prompt);

        var blocked = Array.FindIndex(lines, l => l.Contains(TradeDecisionPromptBuilder.AddOnBlockedReasonLead, StringComparison.Ordinal));
        var exitRule = Array.FindIndex(lines, l => l == $"- {TradeDecisionPromptBuilder.ExitFollowsPolicyRule}");
        var reached = Array.FindIndex(lines, l => l.Contains(TradeDecisionPromptBuilder.TakeProfitReachedLinePrefix, StringComparison.Ordinal));

        blocked.Should().BeGreaterThan(0, "買い増し・売り増しを選べない行がある");
        reached.Should().BeGreaterThan(0, "塞がっていても到達の行は消えない");
        exitRule.Should().Be(blocked + 1);
        reached.Should().Be(exitRule + 1);
        lines[reached].Should().Contain($"手仕舞い（{close}）");
    }

    // T-10-1941（#1129 再監査 Y4）: 一次（門）で、買い増しが審査で塞がっていても（#1142）利確条件への到達の行は消えない。
    // 塞がりの行のすぐ次に到達の行が並ぶ（保有状況の節の中）。
    [Theory]
    [InlineData(10, "106", "Sell")]
    [InlineData(-10, "94", "Buy")]
    public void 一次は買い増しが塞がっていても到達の行を出す(int qty, string mark, string close)
    {
        var held = new HeldPosition(qty, 100m, null);
        var m = decimal.Parse(mark, System.Globalization.CultureInfo.InvariantCulture);
        var section = HeldSection(Screening("利確: 全銘柄 +5%", held, m, [RejectionReason.MaxPositionsExceeded]));
        var lines = section.Split('\n');

        var blocked = Array.FindIndex(lines, l => l.Contains(TradeDecisionPromptBuilder.ScreeningAddOnBlockedTail, StringComparison.Ordinal));
        var reached = Array.FindIndex(lines, l => l.Contains(TradeDecisionPromptBuilder.TakeProfitReachedLinePrefix, StringComparison.Ordinal));

        blocked.Should().BeGreaterThan(0, "買い増し・売り増しを選べない行がある");
        lines[blocked].Should().Contain(TradeDecisionPromptBuilder.AddOnBlockedReasonLead);
        reached.Should().Be(blocked + 1, "塞がっていても到達の行は消えず、塞がりの行の次に並ぶ");
        lines[reached].Should().Contain($"手仕舞い（{close}）");
    }

    // T-10-1945（#1129 第 3 回監査 F4）: 一次の縮退の到達の行の予約は、その銘柄に掛かる読める「利確:」条件があるときだけ掛ける。
    // 条件が無い（他の銘柄の行だけ・書式外の行がある・行が無い）方針は develop と同じ予算で記事を残し、条件がある方針でだけ予約ぶん早く削る。
    [Fact]
    public void 縮退の到達の行の予約は条件があるときだけ掛ける()
    {
        var news = new RetrievedContext("記事", new string('あ', 100), SourceUri: null, 0.5, ["google-news"], null);
        int Dropped(string policy, int budget) =>
            ScreeningContextAssembler.Assemble(Aapl, Policy(policy), [news], currentPrice: null, budget, watchlist: null).Plan.DroppedNewsCount;

        // 同じ長さの 3 つの方針: AAPL の条件あり／他の銘柄の条件だけ／条件なし（「利確」の語を含む書式外の行＝方針全体を読まない。#1129 第 4 回監査 R1）。
        const string withCondition = "利確: AAPL +5%";
        const string otherSymbol = "利確: MSFT +5%";
        const string noCondition = "AAPLは利確 +5%。";
        new[] { otherSymbol.Length, noCondition.Length }.Should().AllBeEquivalentTo(withCondition.Length);

        // 条件の無い方針で記事を 1 件も削らない最小の予算（予約なしの develop と同じ見積り）。
        var budget = Enumerable.Range(1, 20_000).First(b => Dropped(noCondition, b) == 0);

        Dropped(otherSymbol, budget).Should().Be(0, "AAPL に掛かる条件が無ければ予約しない");
        Dropped(noCondition, budget - 1).Should().Be(1);
        Dropped(withCondition, budget).Should().Be(1, "AAPL に掛かる条件があれば到達の行の予約ぶん早く削る");
        Dropped(withCondition, budget + ScreeningContextAssembler.TakeProfitReachedReserveChars).Should().Be(0);
        Dropped(withCondition, budget + ScreeningContextAssembler.TakeProfitReachedReserveChars - 1).Should().Be(1);
    }
}
