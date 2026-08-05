using AiStockTrading.OrderExecution.Infrastructure.Composable.Adapters;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AiStockTrading.OrderExecution.Infrastructure.Tests;

// FR-05, FR-12, ADR-0002, IADR-0111: ブローカ選択の 2 軸（provider × environment）と fail-safe を検証する。
//
// ⚠️ ここでの environment は「実弾を可能にする設定」ではない。live を選んでも LiveTradingGate（閂 0）が
//    起動時に停止させる（LiveTradingGateTests）。既存の SIMULATE 固定 4 層は本 PR で一切変更していない。
public class BrokerSelectionTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (key, value) in entries)
        {
            dict[key] = value;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    // ---- fail-safe 既定（未設定は最も本番から遠い階層へ倒れる）----

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData("paper", null)]
    public void 未設定はpaperのsim階層へ倒れる(string? provider, string? environment)
    {
        var selection = BrokerSelection.Parse(provider, environment);

        selection.Provider.Should().Be(BrokerVendor.Paper);
        selection.Environment.Should().Be(BrokerEnvironment.Simulated);
        selection.IsLive.Should().BeFalse();
        selection.Tier.Should().Be("paper");
    }

    [Fact]
    public void 構成からも同じ既定へ倒れる()
    {
        BrokerSelection.FromConfiguration(Config()).Tier.Should().Be("paper");
    }

    [Fact]
    public void 構成キーは_Broker_Provider_と_Broker_Environment()
    {
        var selection = BrokerSelection.FromConfiguration(Config(
            ("Broker:Provider", "moomoo"),
            ("Broker:Environment", "sim")));

        selection.Should().Be(new BrokerSelection(BrokerVendor.Moomoo, BrokerEnvironment.Simulated));
    }

    // ---- provider × environment の行列（3 階層）----

    // 期待値は enum 名の文字列で与える（internal な enum を public なテストメソッドの引数型にできないため）。
    [Theory]
    [InlineData("paper", "sim", "Paper", "Simulated", "paper")]
    [InlineData("moomoo", "sim", "Moomoo", "Simulated", "moomoo-sim")]
    [InlineData("moomoo", "live", "Moomoo", "Live", "moomoo-live")]
    public void 行列の各組が独立に解決される(
        string provider,
        string environment,
        string expectedProvider,
        string expectedEnvironment,
        string expectedTier)
    {
        var selection = BrokerSelection.Parse(provider, environment);

        selection.Provider.ToString().Should().Be(expectedProvider);
        selection.Environment.ToString().Should().Be(expectedEnvironment);
        selection.Tier.Should().Be(expectedTier);
    }

    [Fact]
    public void 階層名は本番近接順に_paper_moomoo_sim_moomoo_live()
    {
        // 正準名がそのまま運用の本番近接順（paper ＜ シム ＜ 実弾）を表す。introspection の自己申告に使う。
        var tiers = new[]
        {
            BrokerSelection.Parse("paper", null).Tier,
            BrokerSelection.Parse("moomoo", "sim").Tier,
            BrokerSelection.Parse("moomoo", "live").Tier,
        };

        tiers.Should().Equal("paper", "moomoo-sim", "moomoo-live");
    }

    [Theory]
    [InlineData(" MOOMOO ", " Sim ")]
    [InlineData("Moomoo", "SIM")]
    public void 大小文字と前後空白は正規化される(string provider, string environment)
    {
        BrokerSelection.Parse(provider, environment)
            .Should().Be(new BrokerSelection(BrokerVendor.Moomoo, BrokerEnvironment.Simulated));
    }

    [Fact]
    public void moomoo判定は_provider_軸のみで決まる()
    {
        // environment は moomoo かどうかを変えない（2 軸が直交していることの確認）。
        BrokerSelection.Parse("moomoo", "sim").IsMoomoo.Should().BeTrue();
        BrokerSelection.Parse("moomoo", "live").IsMoomoo.Should().BeTrue();
        BrokerSelection.Parse("paper", "sim").IsMoomoo.Should().BeFalse();
    }

    // ---- 不正値は黙って倒さず起動時に停止する（誤設定を隠さない）----

    [Theory]
    [InlineData("etrade")]
    [InlineData("tachibana")]
    public void 未知のプロバイダは停止する(string provider)
    {
        var act = () => BrokerSelection.Parse(provider, "sim");

        act.Should().Throw<InvalidOperationException>().WithMessage("*Broker:Provider*");
    }

    [Theory]
    [InlineData("simulate")]
    [InlineData("real")]
    [InlineData("production")]
    public void 未知の取引環境は停止する_黙ってsimへ倒さない(string environment)
    {
        // 黙って sim へ倒すと誤設定が隠れる。既存 MoomooBrokerOptions.EnsureSimulate と同じ流儀で停止させる。
        var act = () => BrokerSelection.Parse("moomoo", environment);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Broker:Environment*");
    }

    [Fact]
    public void paper_と_live_の同時指定は停止する_実弾のつもりで擬似発注を作らない()
    {
        // paper は environment 非該当（内蔵擬似）。黙殺すると「実弾のつもりで擬似発注」という最悪の誤認が成立する。
        var act = () => BrokerSelection.Parse("paper", "live");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Broker:Provider*").Which.Message.Should().Contain("Broker:Environment");
    }

    // FR-20, FR-12, #386, IADR-0149 決定1: 構成（vendor × environment）を計画の発注先 3 値へ写す。
    // **この写像が「Stage 1 の合格証跡に何が算入されるか」を決める唯一の場所である。**
    // 対応関係は IADR-0140 決定6 のもの。paper が誤って MoomooSimulate に写ると、
    // 外部へ一度も発注していない擬似約定が 100 件の合格証跡へ積み上がる。
    [Theory]
    [InlineData("paper", null, BrokerProvider.InternalPaper)]
    [InlineData("paper", "sim", BrokerProvider.InternalPaper)]
    [InlineData("moomoo", "sim", BrokerProvider.MoomooSimulate)]
    [InlineData("moomoo", null, BrokerProvider.MoomooSimulate)]
    [InlineData("moomoo", "live", BrokerProvider.MoomooReal)]
    public void 構成の発注先が計画の3値へ写る(string provider, string? environment, BrokerProvider expected)
    {
        BrokerSelection.Parse(provider, environment).ToBrokerProvider().Should().Be(expected);
    }

    // **否定形**: 未設定（既定）は内蔵 paper であり、Stage 1 に算入されない側へ倒れる。
    [Fact]
    public void 未設定の発注先は内蔵paperであり合格証跡に算入されない()
    {
        BrokerSelection.FromConfiguration(Config()).ToBrokerProvider()
            .Should().Be(BrokerProvider.InternalPaper);
    }
}
