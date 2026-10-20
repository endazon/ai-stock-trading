using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace RiskManagementService.Tests;

// T-10-2937〜T-10-2941, FR-10, FR-19, #1397, IADR-0562 決定 1・2: テーマ（相関の高い銘柄の集まり）ごとの同時保有の上限。
// 既定は暗号資産（COIN・MARA・MSTR・HOOD）と AI・半導体（NVDA・SMCI）で上限はどちらも 1。未約定の新規建てを数え、どの集まりにも属さない銘柄には掛けない。
public class ThemeConcentrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 15, 0, 0, TimeSpan.Zero);

    private static OrderIntent Intent(string symbol, TradeSide side = TradeSide.Buy, PositionEffect effect = PositionEffect.Open, Market market = Market.UnitedStates) =>
        new(symbol, market, side, ProductType.Cash, BrokerProvider.InternalPaper, 10, 20m, effect);

    private static PortfolioSnapshot Holding(params string[] symbols) => new()
    {
        Capital = 100_000m,
        OpenPositionCount = 0,
        HeldOrPendingSymbols = symbols.Select(s => (s, Market.UnitedStates)).ToHashSet(),
    };

    private static OrderScreeningResult Evaluate(OrderIntent intent, PortfolioSnapshot snapshot, ThemeConcentrationSettings? themes = null) =>
        RiskEvaluator.Evaluate(intent, TradingDefaults.CreateSettings(), snapshot, themes: themes ?? TradingDefaults.CreateThemeConcentration());

    [Theory]
    [InlineData("MARA", "COIN")]
    [InlineData("COIN", "HOOD")]
    [InlineData("SMCI", "NVDA")]
    [InlineData("nvda", "SMCI")] // 大文字小文字を区別しない
    public void T_10_2937_同じ集まりの別の銘柄を保有中なら新規建てを拒否する(string held, string entry)
    {
        var result = Evaluate(Intent(entry), Holding(held));

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().ContainSingle().Which.Should().Be(RejectionReason.ThemeConcentrationExceeded);
    }

    [Fact]
    public void T_10_2937_同じ銘柄の買い増しは集まりの銘柄の数を増やさないので通す()
    {
        Evaluate(Intent("MARA"), Holding("MARA")).IsApproved.Should().BeTrue();
    }

    [Theory]
    [InlineData("NVDA")] // 別の集まり
    [InlineData("AAPL")] // どの集まりにも属さない
    public void T_10_2937_別の集まりと集まりに属さない銘柄には掛けない(string entry)
    {
        Evaluate(Intent(entry), Holding("MARA")).IsApproved.Should().BeTrue();
        Evaluate(Intent("AAPL"), Holding("MARA", "NVDA", "TSLA")).IsApproved.Should().BeTrue();
    }

    // 🔴 手仕舞い（Close）は止めない（ADR-0009）。空売りの新規建ても新規建てとして数える（方向は問わない）。
    [Fact]
    public void T_10_2937_手仕舞いは止めず空売りの新規建ては止める()
    {
        Evaluate(Intent("COIN", TradeSide.Sell, PositionEffect.Close), Holding("MARA", "COIN")).IsApproved.Should().BeTrue();

        var shortSettings = TradingDefaults.CreateSettings() with
        {
            Guard = TradingDefaults.CreateGuardSettings() with
            {
                EnabledProductTypes = new HashSet<ProductType> { ProductType.Cash, ProductType.MarginLong, ProductType.ShortSell },
            },
        };
        RiskEvaluator.Evaluate(
                Intent("COIN", TradeSide.Sell) with { ProductType = ProductType.ShortSell }, shortSettings, Holding("MARA"),
                themes: TradingDefaults.CreateThemeConcentration())
            .Reasons.Should().Contain(RejectionReason.ThemeConcentrationExceeded);
    }

    [Fact]
    public void T_10_2937_市場の違う同じコードは数えない()
    {
        var snapshot = new PortfolioSnapshot
        {
            Capital = 100_000m,
            HeldOrPendingSymbols = new HashSet<(string, Market)> { ("MARA", Market.Japan) },
        };

        Evaluate(Intent("COIN"), snapshot).IsApproved.Should().BeTrue();
    }

    [Fact]
    public void T_10_2937_上限が2なら2銘柄目までは通し3銘柄目を拒否する()
    {
        var themes = new ThemeConcentrationSettings([new ThemeLimit("暗号資産", Market.UnitedStates, ["COIN", "MARA", "MSTR"], 2)]);

        Evaluate(Intent("COIN"), Holding("MARA"), themes).IsApproved.Should().BeTrue();
        Evaluate(Intent("COIN"), Holding("MARA", "MSTR"), themes).Reasons.Should().Contain(RejectionReason.ThemeConcentrationExceeded);
    }

    [Fact]
    public void T_10_2937_定義の供給が無ければ評価しない()
    {
        RiskEvaluator.Evaluate(Intent("COIN"), TradingDefaults.CreateSettings(), Holding("MARA")).IsApproved.Should().BeTrue();
    }

    // 入力（保有中 ∪ 未約定の新規建て）の射影。
    [Fact]
    public void T_10_2938_保有中と未約定の新規建ての銘柄を数え手仕舞った銘柄は数えない()
    {
        var fills = new List<LedgerFill>
        {
            new("MARA", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 100, 20m, Now.AddHours(-2)),
            new("NVDA", Market.UnitedStates, TradeSide.Sell, PositionEffect.Open, 10, 180m, Now.AddHours(-2)),
            new("HOOD", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 10, 50m, Now.AddHours(-3)),
            new("HOOD", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, 10, 51m, Now.AddHours(-1)),
        };
        var working = new List<WorkingEntryOrder> { new(Guid.NewGuid(), "COIN", Market.UnitedStates, TradeSide.Buy, 5, 300m, Now.AddMinutes(-5)) };

        var state = PortfolioProjection.Project(fills, Now, 100_000m, workingEntries: working);

        state.HeldOrPendingSymbols.Should().BeEquivalentTo(
            [("MARA", Market.UnitedStates), ("NVDA", Market.UnitedStates), ("COIN", Market.UnitedStates)]);
    }

    [Fact]
    public void T_10_2940_構成が無ければ既定の2つの集まり()
    {
        var themes = ThemeConcentrationLoader.FromConfiguration(new ConfigurationBuilder().Build()).Themes;

        themes.Should().HaveCount(2);
        themes[0].Should().BeEquivalentTo(new ThemeLimit("暗号資産", Market.UnitedStates, ["COIN", "MARA", "MSTR", "HOOD"], 1));
        themes[1].Should().BeEquivalentTo(new ThemeLimit("AI・半導体", Market.UnitedStates, ["NVDA", "SMCI"], 1));
    }

    [Fact]
    public void T_10_2940_構成があれば既定を丸ごと置き換える()
    {
        var themes = ThemeConcentrationLoader.FromConfiguration(Config(new Dictionary<string, string?>
        {
            ["0:Name"] = "EV",
            ["0:MaxPositions"] = "2",
            ["0:Symbols:0"] = "tsla",
            ["0:Symbols:1"] = "RIVN",
        })).Themes;

        themes.Should().ContainSingle().Which.Should().BeEquivalentTo(new ThemeLimit("EV", Market.UnitedStates, ["TSLA", "RIVN"], 2));
    }

    [Theory]
    [InlineData("", "1", "COIN", null)]
    [InlineData("暗号資産", "0", "COIN", null)]
    [InlineData("暗号資産", "x", "COIN", null)]
    [InlineData("暗号資産", "1", null, null)]
    [InlineData("暗号資産", "1", "COIN", "Europe")]
    public void T_10_2940_誤った定義は起動を止める(string name, string max, string? symbol, string? market)
    {
        var values = new Dictionary<string, string?> { ["0:Name"] = name, ["0:MaxPositions"] = max };
        if (symbol is not null)
            values["0:Symbols:0"] = symbol;
        if (market is not null)
            values["0:Market"] = market;

        var act = () => ThemeConcentrationLoader.FromConfiguration(Config(values));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void T_10_2940_名前の重複は起動を止める()
    {
        var act = () => ThemeConcentrationLoader.FromConfiguration(Config(new Dictionary<string, string?>
        {
            ["0:Name"] = "A",
            ["0:MaxPositions"] = "1",
            ["0:Symbols:0"] = "COIN",
            ["1:Name"] = "A",
            ["1:MaxPositions"] = "1",
            ["1:Symbols:0"] = "NVDA",
        }));

        act.Should().Throw<InvalidOperationException>();
    }

    private static IConfiguration Config(IDictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(kv =>
                new KeyValuePair<string, string?>($"{ThemeConcentrationLoader.Section}:{kv.Key}", kv.Value)))
            .Build();
}
