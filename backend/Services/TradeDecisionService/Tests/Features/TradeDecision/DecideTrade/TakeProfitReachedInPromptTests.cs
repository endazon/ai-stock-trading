extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-04, ADR-0003, #1129, IADR-0470 決定 3（オーナー裁定 2026-10-01）: 判断は LLM が方針を見て行うまま、方針に数値の利確条件があり
// それに達しているときだけ、保有状況の節で「方針の利確条件に達している」と明示する。数値（含み益の率・条件との比較）はコードで計算する。
// 既定の Hold の規則（ExitFollowsPolicyRule）は変えない。自動の利確は採らない。T-10-1889〜1891。
public class TakeProfitReachedInPromptTests
{
    private static readonly SizingContext Context =
        new(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

    private static readonly DecisionTrigger Aapl = DecisionTrigger.Scheduled("AAPL", Market.UnitedStates);

    private static DailyPolicy Policy(string text) => new(new DateOnly(2026, 10, 1), text);

    private static string MainPrompt(string policy, HeldPosition? held, decimal? price) =>
        TradeDecisionPromptBuilder.Build(Aapl, Policy(policy), Context, currentPrice: price, held: held,
            working: WorkingEntryOrders.None, watchlist: []);

    private static string Screening(string policy, HeldPosition? held, decimal? price) =>
        TradeDecisionPromptBuilder.BuildScreening(Aapl, Policy(policy), Context, currentPrice: price, held: held,
            working: WorkingEntryOrders.None, watchlist: []);

    // T-10-1889: ロング 100→106（+6%）で方針の +5% に達している。本判断・一次の両方の保有状況の節に、条件と系が計算した率を書く。
    [Fact]
    public void 方針の利確条件に達していれば本判断と一次に明示する()
    {
        const string policy = "AAPL: 取得単価から +5% で保有の 50% を利確。";
        var held = new HeldPosition(10, 100m, 98m);

        foreach (var prompt in new[] { MainPrompt(policy, held, 106m), Screening(policy, held, 106m) })
        {
            var line = prompt.Split('\n').Select(l => l.TrimEnd('\r'))
                .Should().ContainSingle(l => l.Contains(TradeDecisionPromptBuilder.TakeProfitReachedLinePrefix)).Which;
            line.Should().Contain("取得単価から +5% で利確（一部利確 50%）")
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

    // T-10-1889: ショートは値下がりが含み益（100→94 で +6%）。手仕舞いは Buy。
    [Fact]
    public void ショートは値下がりを含み益として比べる()
    {
        var prompt = MainPrompt("+5% で利確", new HeldPosition(-10, 100m, 102m), 94m);

        prompt.Should().Contain(TradeDecisionPromptBuilder.TakeProfitReachedLinePrefix).And.Contain("+6.00%").And.Contain("手仕舞い（Buy）");
    }

    // T-10-1890（否定形）: 達していない・数値の利確条件が無い・他の銘柄の条件・取得単価や現在値が不明・保有なしでは何も書かない
    // （プロンプトは利確条件の無い方針と一字一句同じ）。既定の Hold の規則は変えない。
    [Theory]
    [InlineData("AAPL: 取得単価から +5% で利確。", 10, "100", "104.99")]
    [InlineData("含み益が十分に出た段階で利確する。", 10, "100", "150")]
    [InlineData("MSFT: +5% で利確。", 10, "100", "150")]
    [InlineData("AAPL: +5% で利確。", 10, null, "150")]
    [InlineData("AAPL: +5% で利確。", 10, "100", null)]
    [InlineData("AAPL: +5% で利確。", 0, "100", "150")]
    public void 達していないか確かめられなければ何も書かない(string policy, int qty, string? entry, string? price)
    {
        var e = entry is null ? (decimal?)null : decimal.Parse(entry, System.Globalization.CultureInfo.InvariantCulture);
        var p = price is null ? (decimal?)null : decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture);
        var held = new HeldPosition(qty, qty == 0 ? null : e, null);

        var prompt = MainPrompt(policy, held, p);
        var screening = Screening(policy, held, p);

        prompt.Should().NotContain(TradeDecisionPromptBuilder.TakeProfitReachedLinePrefix);
        screening.Should().NotContain(TradeDecisionPromptBuilder.TakeProfitReachedLinePrefix);
        // 方針の文以外は、利確条件の無い方針と同じ。
        prompt.Replace(policy, "X", StringComparison.Ordinal)
            .Should().Be(MainPrompt("X", held, p));
        if (qty != 0)
            prompt.Should().Contain(TradeDecisionPromptBuilder.ExitFollowsPolicyRule);
    }

    // T-10-1891: 行の最悪長（3 件を並べ・桁の多い値・通貨表記・一部利確の注記）が一次の縮退の保護分の予約に収まる。
    [Fact]
    public void 行の最悪長は縮退の予約に収まる()
    {
        const string policy = "全銘柄: +1.2345% で保有の 33.3333% を利確、+2.2345% で 33.3333%を利確、+3.2345% で 33.3333%を利確、+4.5% で利確、$1.5 で利確";
        var held = new HeldPosition(-1_000_000, 9_999_999.9999m, null);
        var line = TradeDecisionPromptBuilder.TakeProfitReachedLine(policy, "AAPL", held, 0.0001m, " JPY");

        line.Should().NotBeNull();
        line.Should().Contain(" ほか ");
        ("- " + line + "\n").Length.Should().BeLessThanOrEqualTo(ScreeningContextAssembler.TakeProfitReachedReserveChars);
    }
}
