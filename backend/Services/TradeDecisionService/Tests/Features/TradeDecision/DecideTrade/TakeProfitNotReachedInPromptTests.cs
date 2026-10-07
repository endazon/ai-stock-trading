extern alias RiskManagementWorker;

using System.Globalization;
using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-04, ADR-0003, ADR-0051, #1175, IADR-0470（2026-10-07 追記・オーナー裁定 2026-10-07）: 方針の「利確:」行が読めて比べられ、未到達の保有は
// 保有状況の節（本判断・一次）で「方針の利確条件に未到達（現在 +x.xx% / 基準 +y%）」と明示する（LLM の裁量は残す＝売りを禁じない）。
// 含み損益率は小数 2 桁の丸めのまま、直後に到達判定の注記を並べる（丸めた数字だけで到達と読ませない）。判定は丸めない比較。
// T-10-2331〜2334。
public class TakeProfitNotReachedInPromptTests
{
    private static readonly SizingContext Context =
        new(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

    private static readonly DecisionTrigger Aapl = DecisionTrigger.Scheduled("AAPL", Market.UnitedStates);

    private static DailyPolicy Policy(string text) => new(new DateOnly(2026, 10, 7), text);

    private static string MainPrompt(
        string policy, HeldPosition held, decimal price, IReadOnlyList<RejectionReason>? addOnBlockers = null) =>
        TradeDecisionPromptBuilder.Build(Aapl, Policy(policy), Context, currentPrice: price, held: held,
            working: WorkingEntryOrders.None, watchlist: [], addOnBlockers: addOnBlockers);

    private static string Screening(
        string policy, HeldPosition held, decimal price, IReadOnlyList<RejectionReason>? addOnBlockers = null) =>
        TradeDecisionPromptBuilder.BuildScreening(Aapl, Policy(policy), Context, currentPrice: price, held: held,
            working: WorkingEntryOrders.None, watchlist: [], addOnBlockers: addOnBlockers);

    private static string[] Lines(string prompt) => prompt.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    private static string NotReachedLine(string prompt) =>
        Lines(prompt).Should().ContainSingle(l => l.Contains(TradeDecisionPromptBuilder.TakeProfitNotReachedLinePrefix)).Which;

    private static decimal D(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);

    // T-10-2331: PoC の NVDA（230.77→237.69＝+2.9986%）と方針「利確: 全銘柄 +3%」。丸めた率は +3.00% だが未到達。
    // 本判断は「含み損益」の率の直後、一次は「含み損益率」の直後に未到達の注記。未到達の行は出口の既定の規則の次（本判断）・一次の規則の次に並ぶ。
    [Fact]
    public void 丸めると基準と同じに見える未到達を本判断と一次で明示する()
    {
        const string policy = "押し目で拾い、含み益が出たら売る。\n利確: 全銘柄 +3%";
        var held = new HeldPosition(10, 230.77m, 220m);

        var main = MainPrompt(policy, held, 237.69m);
        var screening = Screening(policy, held, 237.69m);

        foreach (var prompt in new[] { main, screening })
        {
            NotReachedLine(prompt).Should().StartWith("- 方針の利確条件に未到達（現在 +3.00% / 基準 +3%）: ")
                .And.Contain("平均取得単価 230.77 と現在値 237.69 から計算し、丸めずに比べた結果です。")
                .And.Contain("手仕舞い（Sell）を選ぶかは、方針とリスク制約に照らして判断します。")
                .And.NotContain("すべてに達したとき");
            prompt.Should().NotContain(TradeDecisionPromptBuilder.TakeProfitReachedLinePrefix)
                .And.NotContain(TradeDecisionPromptBuilder.TakeProfitReachedNote);
        }

        Lines(main).Should().Contain(l => l.StartsWith("- 含み損益: ", StringComparison.Ordinal)
            && l.Contains("（+3.00%（方針の利確条件: 未到達）・現在値 237.69 で評価）", StringComparison.Ordinal));
        Lines(screening).Should().Contain(l => l.Contains("/ 含み損益率: +3.00%（方針の利確条件: 未到達） /", StringComparison.Ordinal));

        var mainLines = Lines(main);
        var exitRule = Array.FindIndex(mainLines, l => l == $"- {TradeDecisionPromptBuilder.ExitFollowsPolicyRule}");
        Array.FindIndex(mainLines, l => l.Contains(TradeDecisionPromptBuilder.TakeProfitNotReachedLinePrefix, StringComparison.Ordinal))
            .Should().Be(exitRule + 1);
        var screeningLines = Lines(screening);
        var heldRule = Array.FindIndex(screeningLines, l => l.Contains(TradeDecisionPromptBuilder.ScreeningHeldRule, StringComparison.Ordinal));
        Array.FindIndex(screeningLines, l => l.Contains(TradeDecisionPromptBuilder.TakeProfitNotReachedLinePrefix, StringComparison.Ordinal))
            .Should().Be(heldRule + 1);
    }

    // T-10-2331（T-10-1890 から移した場面）: 未到達・同じ銘柄の 2 行の片方だけ到達・全銘柄には到達し名指しの行は未到達。
    // いずれも未到達の行と注記を出し、到達の行は出さない（買い増しの塞がりなし／あり）。既定の Hold の規則は残る。
    [Theory]
    [InlineData("利確: AAPL +5%", "104.99", "現在 +4.99% / 基準 +5%")]
    [InlineData("利確: AAPL +5%\n利確: AAPL +8%", "106", "現在 +6.00% / 基準 +5%・+8%")]
    [InlineData("利確: 全銘柄 +3%\n利確: AAPL +8%", "106", "現在 +6.00% / 基準 +8%")]
    public void 比べられて未到達なら未到達の行と注記を出す(string policy, string mark, string expectedBracket)
    {
        var held = new HeldPosition(10, 100m, null);

        foreach (var blockers in new IReadOnlyList<RejectionReason>?[] { null, [RejectionReason.MaxPositionsExceeded] })
        {
            foreach (var prompt in new[] { MainPrompt(policy, held, D(mark), blockers), Screening(policy, held, D(mark), blockers) })
            {
                NotReachedLine(prompt).Should().Contain($"（{expectedBracket}）");
                prompt.Should().Contain(TradeDecisionPromptBuilder.TakeProfitNotReachedNote)
                    .And.NotContain(TradeDecisionPromptBuilder.TakeProfitReachedLinePrefix)
                    .And.NotContain(TradeDecisionPromptBuilder.TakeProfitReachedNote);
            }

            MainPrompt(policy, held, D(mark), blockers).Should().Contain(TradeDecisionPromptBuilder.ExitFollowsPolicyRule);
        }
    }

    // T-10-2332: 行の書き分け。ショート（含み損でも未到達と書く・手仕舞いは Buy）・価格だけ（現在は現在値）・率と価格の混在（両方を並べる）・
    // 複数の条件（「すべてに達したときが到達」）・4 件以上（3 件まで＋「ほか N 件」）。
    [Theory]
    [InlineData("利確: AAPL +5%", -10, "100", "104", "（現在 -4.00% / 基準 +5%）", "手仕舞い（Buy）", false)]
    [InlineData("利確: AAPL +5%", -10, "100", "96", "（現在 +4.00% / 基準 +5%）", "手仕舞い（Buy）", false)]
    [InlineData("利確: AAPL $230", 10, "200", "229.99", "（現在 229.99 / 基準 230）", "手仕舞い（Sell）", false)]
    [InlineData("利確: AAPL 190 ドル", -10, "200", "190.5", "（現在 190.5 / 基準 190）", "手仕舞い（Buy）", false)]
    [InlineData("利確: AAPL +5%\n利確: AAPL $230", 10, "200", "220", "（現在 +10.00%・220 / 基準 +5%・230）", "手仕舞い（Sell）", true)]
    [InlineData("利確: AAPL +1%\n利確: AAPL +2%\n利確: AAPL +3%\n利確: AAPL +40%\n利確: AAPL +50%", 10, "100", "110",
        "（現在 +10.00% / 基準 +1%・+2%・+3%・ほか 2 件）", "手仕舞い（Sell）", true)]
    public void 未到達の行は現在と基準を条件の種類と件数で書き分ける(
        string policy, int qty, string entry, string mark, string bracket, string close, bool multiple)
    {
        var held = new HeldPosition(qty, D(entry), null);

        foreach (var prompt in new[] { MainPrompt(policy, held, D(mark)), Screening(policy, held, D(mark)) })
        {
            var line = NotReachedLine(prompt);
            line.Should().StartWith("- " + TradeDecisionPromptBuilder.TakeProfitNotReachedLinePrefix + bracket + ": ")
                .And.Contain(close)
                .And.Contain($"平均取得単価 {entry} と現在値 {mark} から計算し、丸めずに比べた結果です。");
            if (multiple)
                line.Should().Contain("基準が複数あるときは、すべてに達したときが到達です。");
            else
                line.Should().NotContain("すべてに達したとき");
            if (qty < 0)
                line.Should().NotContain("Sell");
        }
    }

    // T-10-2333: 到達なら到達の行（従来の文言）と、含み損益率の直後の「（方針の利確条件: 到達）」を出す。未到達の行・注記は出さない。
    // ちょうどは到達（ロング 100→103 と +3%）。
    [Theory]
    [InlineData(10, "103", "+3.00%")]
    [InlineData(-10, "97", "+3.00%")]
    public void 到達なら到達の行と到達の注記を出す(int qty, string mark, string ratio)
    {
        var held = new HeldPosition(qty, 100m, null);
        var main = MainPrompt("利確: 全銘柄 +3%", held, D(mark));
        var screening = Screening("利確: 全銘柄 +3%", held, D(mark));

        foreach (var prompt in new[] { main, screening })
        {
            Lines(prompt).Should().ContainSingle(l => l.Contains(TradeDecisionPromptBuilder.TakeProfitReachedLinePrefix, StringComparison.Ordinal));
            prompt.Should().Contain(TradeDecisionPromptBuilder.TakeProfitReachedNote)
                .And.NotContain(TradeDecisionPromptBuilder.TakeProfitNotReachedLinePrefix)
                .And.NotContain(TradeDecisionPromptBuilder.TakeProfitNotReachedNote);
        }

        Lines(main).Should().Contain(l => l.StartsWith("- 含み損益: ", StringComparison.Ordinal)
            && l.Contains($"（{ratio}（方針の利確条件: 到達）・現在値 {mark} で評価）", StringComparison.Ordinal));
        Lines(screening).Should().Contain(l => l.Contains($"/ 含み損益率: {ratio}（方針の利確条件: 到達） /", StringComparison.Ordinal));
    }

    // T-10-2334: 未到達の行・到達の行の最悪長（桁の多い値・通貨表記・3 件と「ほか 2 件」・複数の注記）に含み損益率の注記を足しても、
    // 一次の縮退の保護分の予約（条件があるときだけ掛かる）に収まる。
    [Fact]
    public void 未到達と到達の行の最悪長に注記を足しても縮退の予約に収まる()
    {
        // 未到達: 価格の条件 2 件（利益の側・現在値より上＝未到達）と率の条件 3 件。現在は率と現在値の両方を並べる。
        var notReachedPolicy = string.Join('\n',
            Enumerable.Repeat("利確: AAPL 999,999,999,999.9999 円", 2).Concat(Enumerable.Repeat("利確: AAPL +999,999,999,999.9999%", 3)));
        var held = new HeldPosition(1_000_000, 0.0001m, null);
        var notReached = TradeDecisionPromptBuilder.TakeProfitNotReachedLine(
            notReachedPolicy, "AAPL", held, 999_999_999_999.9998m, " JPY", Currency.Jpy);

        notReached.Should().NotBeNull();
        notReached.Should().Contain("・ほか 2 件").And.Contain("すべてに達したときが到達");
        (("- " + notReached + "\n").Length + TradeDecisionPromptBuilder.TakeProfitNotReachedNote.Length)
            .Should().BeLessThanOrEqualTo(ScreeningContextAssembler.TakeProfitReachedReserveChars);

        // 到達: T-10-1891 と同じ最悪の入力（率の条件 5 件・一部利確の注記）。注記を足しても収まる。
        var reachedPolicy = string.Join('\n', Enumerable.Repeat("利確: AAPL +999,999,999,999.9999% (99.9999%)", 5));
        var reached = TradeDecisionPromptBuilder.TakeProfitReachedLine(reachedPolicy, "AAPL", held, 999_999_999_999.9999m, " JPY", Currency.Jpy);

        reached.Should().NotBeNull();
        (("- " + reached + "\n").Length + TradeDecisionPromptBuilder.TakeProfitReachedNote.Length)
            .Should().BeLessThanOrEqualTo(ScreeningContextAssembler.TakeProfitReachedReserveChars);
    }
}
