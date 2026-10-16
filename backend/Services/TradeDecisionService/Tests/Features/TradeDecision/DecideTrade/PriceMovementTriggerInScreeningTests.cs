extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-03, FR-04, ADR-0003, #1364: 急変（PriceMovementDetected）が起点の判断で、一次スクリーニングのプロンプトにも
// 本判断と同じ「# 価格変動トリガー」節（現在値・基準値・変動率）を出す。
// 実測（PoC 2026-10-15）: 一次に基準値と変動率が無く、方針の「C（急変追随）」の条件を一次が判定できず、急変 5 件がすべて
// 「急変の検知の基準値と変動率が提供されておらず判定できない」で落ちた（一次は門＝Hold で本判断が走らない）。実 LLM は呼ばない。
public class PriceMovementTriggerInScreeningTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 13, 31, 46, TimeSpan.Zero);

    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 10, 15), "C（急変追随）: 基準値から +3% 以上の急変は追随の買いを検討する。");

    private static readonly SizingContext Context =
        new(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

    private const string TriggerHeading = "# 価格変動トリガー";

    private static DecisionTrigger Movement(string symbol = "MARA", Market market = Market.UnitedStates) =>
        DecisionTrigger.FromPriceMovement(
            new PriceMovementDetected(Guid.NewGuid(), symbol, market, 20.80m, 20.00m, 0.04m, Now));

    private static DecisionTrigger Scheduled() => DecisionTrigger.Scheduled("MARA", Market.UnitedStates, Now);

    [Fact]
    public void 急変が起点の一次のプロンプトに現在値と基準値と変動率が含まれる()
    {
        var prompt = TradeDecisionPromptBuilder.BuildScreening(Movement(), Policy, Context, currentPrice: 20.75m);

        prompt.Should().Contain(TriggerHeading);
        prompt.Should().Contain("- 現在値: 20.80 / 基準値: 20.00 / 変動率: 4.00 %");
    }

    // 🔴 本判断と一次で同じ節を出す（片方だけ文言が変わると、一次と本判断で急変の材料が食い違う）。
    [Theory]
    [InlineData(Market.UnitedStates)]
    [InlineData(Market.Japan)]
    public void 一次と本判断は同じ価格変動トリガーの節を出す(Market market)
    {
        var trigger = Movement(market == Market.Japan ? "7203" : "MARA", market);
        var section = TradeDecisionPromptBuilder.PriceMovementTriggerSection(trigger);

        section.Should().StartWith(TriggerHeading);
        TradeDecisionPromptBuilder.BuildScreening(trigger, Policy, Context).Should().Contain(section);
        TradeDecisionPromptBuilder.Build(trigger, Policy, Context).Should().Contain(section);
    }

    [Fact]
    public void 日本株の急変では価格に通貨の単位を付ける()
    {
        var section = TradeDecisionPromptBuilder.PriceMovementTriggerSection(Movement("7203", Market.Japan));

        section.Should().Contain("- 現在値: 20.80 JPY / 基準値: 20.00 JPY / 変動率: 4.00 %");
    }

    // 現在値の供給が無い構成（既定 NoOp）でも、急変の検知の価格だけは一次に届く。
    [Fact]
    public void 現在値の供給が無くても急変の価格と基準値は一次に届く()
    {
        var prompt = TradeDecisionPromptBuilder.BuildScreening(Movement(), Policy, Context, currentPrice: null);

        prompt.Should().Contain("- 現在値: 20.80 / 基準値: 20.00 / 変動率: 4.00 %");
    }

    // 否定形: 定時が起点なら節を出さない（従来の一次のプロンプトのまま）。
    [Fact]
    public void 定時が起点の一次には価格変動トリガーの節が出ない_否定形()
    {
        TradeDecisionPromptBuilder.PriceMovementTriggerSection(Scheduled()).Should().BeEmpty();

        var prompt = TradeDecisionPromptBuilder.BuildScreening(Scheduled(), Policy, Context, currentPrice: 20.75m);

        prompt.Should().NotContain(TriggerHeading);
        prompt.Should().NotContain("基準値:");
        prompt.Should().NotContain("変動率:");
    }

    // 否定形: 価格を持たない急変（契約上は起こり得るが価格なし）は本判断と同じく節を出さない。
    [Fact]
    public void 価格の無い急変では節を出さない_否定形()
    {
        var trigger = new DecisionTrigger("MARA", Market.UnitedStates, DecisionTriggerKind.PriceMovement);

        TradeDecisionPromptBuilder.PriceMovementTriggerSection(trigger).Should().BeEmpty();
        TradeDecisionPromptBuilder.BuildScreening(trigger, Policy, Context).Should().NotContain(TriggerHeading);
    }

    // 🔴 縮退の見積り: 急変の節は保護分であり、見積りは節の実際の長さを数える。定時でちょうど収まる予算を、急変では
    // 節の長さだけ足さないと収まらない（節を見積りから外すと、急変でも削られず赤になる）。
    [Fact]
    public void 縮退の見積りは急変の節の長さを保護分に数える_境界値()
    {
        var news = new RetrievedContext("記事", new string('あ', 100), SourceUri: null, 0.5, ["google-news"], Now);
        var scheduled = Scheduled();
        var movement = Movement();
        var sectionChars = TradeDecisionPromptBuilder.PriceMovementTriggerSection(movement).Length;
        sectionChars.Should().BePositive();

        // 定時で材料 1 件が予算ちょうどに収まる最小の予算を探す（見積りの内訳を試験へ複写しない）。
        var exact = Enumerable.Range(1, 20_000)
            .First(b => ScreeningContextAssembler.Assemble(scheduled, Policy, [news], null, b, watchlist: null)
                .Plan.DroppedNewsCount == 0);

        ScreeningContextAssembler.Assemble(movement, Policy, [news], null, exact, watchlist: null)
            .Plan.DroppedNewsCount.Should().Be(1, "急変の節の分だけ保護分が増え、同じ予算では材料が削られる");
        ScreeningContextAssembler.Assemble(movement, Policy, [news], null, exact + sectionChars - 1, watchlist: null)
            .Plan.DroppedNewsCount.Should().Be(1, "節の長さに 1 文字足りない");
        ScreeningContextAssembler.Assemble(movement, Policy, [news], null, exact + sectionChars, watchlist: null)
            .Plan.DroppedNewsCount.Should().Be(0, "節の長さを足せば収まる");
    }
}
