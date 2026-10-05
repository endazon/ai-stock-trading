using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using TradeDecisionService.Features.TradeDecision;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-02, NFR-02, #1169, IADR-0490 決定1・決定2: 定時サイクルの実行時間の上限の導出と、定時判断の DecisionId の決定的な導出（純関数）。
public class ScheduledCycleBudgetTests
{
    // T-10-2240: 上限は「監視銘柄数の前提 × (LLM の timeout × 1 判断あたりの呼び出し回数 ＋ 30 秒) ＋ 60 秒」。
    // 前提の監視銘柄数・LLM の timeout・呼び出し回数のどれを変えても導き直される（数字を直書きしない）。
    [Theory]
    [InlineData(30, 2, 6, 90, 600)]   // 稼働の PoC: timeout 既定 30 秒・二段（一次＋二次 1 票）・監視 6 銘柄
    [InlineData(30, 2, 10, 90, 960)]  // 既定の前提 10 銘柄
    [InlineData(30, 2, 7, 90, 690)]   // 前提を 1 増やすと 1 銘柄分だけ伸びる
    [InlineData(20, 2, 6, 70, 480)]   // LLM の timeout を変える
    [InlineData(30, 4, 6, 150, 960)]  // 多数決 3 票＋一次
    [InlineData(30, 1, 1, 60, 120)]   // 単発判断・1 銘柄
    public void T_10_2240_上限は前提の監視銘柄数と1銘柄の締め切りから導かれる(
        int llmTimeoutSeconds, int llmCalls, int maxWatched, int expectedPerSymbolSeconds, int expectedHandlerSeconds)
    {
        var budget = ScheduledCycleBudget.Derive(TimeSpan.FromSeconds(llmTimeoutSeconds), llmCalls, maxWatched);

        budget.PerSymbol.Should().Be(TimeSpan.FromSeconds(expectedPerSymbolSeconds));
        budget.HandlerTimeout.Should().Be(TimeSpan.FromSeconds(expectedHandlerSeconds));
        budget.HandlerTimeoutSeconds.Should().Be(expectedHandlerSeconds);
        budget.MaxWatchedSymbols.Should().Be(maxWatched);
        budget.HandlerTimeout.Should().BeGreaterThan(TimeSpan.FromSeconds(60), "Wolverine の既定 60 秒より長い（#1169 の崖）");
    }

    // T-10-2240（否定形）: 端数は切り上げる（切り捨てると上限が導いた値より短くなる）。
    [Fact]
    public void T_10_2240_秒の端数は切り上げる()
    {
        var budget = ScheduledCycleBudget.Derive(
            TimeSpan.FromMilliseconds(1500), 1, 1, TimeSpan.Zero, TimeSpan.Zero);

        budget.HandlerTimeoutSeconds.Should().Be(2);
    }

    // T-10-2241: 1 判断あたりの LLM 呼び出し回数＝一次スクリーニング（有効なら 1）＋二次の票数。監視銘柄数の前提の読み取りは
    // 未設定・不正・非正値で既定 10 へ倒す（0 や負で上限を潰さない）。
    [Theory]
    [InlineData(true, 1, 2)]
    [InlineData(false, 1, 1)]
    [InlineData(true, 3, 4)]
    [InlineData(false, 3, 3)]
    public void T_10_2241_LLM呼び出し回数は一次と二次の票数の和(bool screening, int votes, int expected)
    {
        var options = new DecisionOrchestrationOptions { EnableScreening = screening, VoteCount = votes };

        ScheduledCycleBudget.LlmCallsPerDecision(options).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData("", 10)]
    [InlineData("abc", 10)]
    [InlineData("0", 10)]
    [InlineData("-3", 10)]
    [InlineData("6", 6)]
    [InlineData("25", 25)]
    public void T_10_2241_監視銘柄数の前提は不正なら既定へ倒す(string? configured, int expected) =>
        ScheduledCycleBudget.ParseMaxWatchedSymbols(configured).Should().Be(expected);

    // T-10-2242（否定形）: 導出の入力が非正なら作らない（0 秒の上限で全サイクルが即座に打ち切られる事故を作らない）。
    [Fact]
    public void T_10_2242_非正の入力では導かない()
    {
        var act0 = () => ScheduledCycleBudget.Derive(TimeSpan.Zero, 2, 6);
        var act1 = () => ScheduledCycleBudget.Derive(TimeSpan.FromSeconds(30), 0, 6);
        var act2 = () => ScheduledCycleBudget.Derive(TimeSpan.FromSeconds(30), 2, 0);

        act0.Should().Throw<ArgumentOutOfRangeException>();
        act1.Should().Throw<ArgumentOutOfRangeException>();
        act2.Should().Throw<ArgumentOutOfRangeException>();
    }

    // 🔴 T-10-2244: 定時判断の DecisionId は (起点イベント, 市場, 銘柄) で決まる。同じなら同じ、どれかが違えば違う。
    // 起点イベントの ID が空なら導かない（空から導くと以後の判断がすべて同じ ID になり、下流が再配送として捨てる）。
    [Fact]
    public void T_10_2244_定時判断のDecisionIdは起点イベントと市場と銘柄で決まる()
    {
        var eventId = Guid.NewGuid();

        var a1 = ScheduledDecisionIds.For(eventId, "AAPL", Market.UnitedStates);
        var a2 = ScheduledDecisionIds.For(eventId, "AAPL", Market.UnitedStates);

        a1.Should().NotBeNull();
        a2.Should().Be(a1, "再配送でも同じ DecisionId");
        ScheduledDecisionIds.For(eventId, "MSFT", Market.UnitedStates)!.Value.Should().NotBe(a1!.Value, "銘柄が違えば別の判断");
        ScheduledDecisionIds.For(eventId, "AAPL", Market.Japan)!.Value.Should().NotBe(a1!.Value, "市場が違えば別の判断");
        ScheduledDecisionIds.For(Guid.NewGuid(), "AAPL", Market.UnitedStates)!.Value.Should().NotBe(a1!.Value, "サイクルが違えば別の判断");
        ScheduledDecisionIds.For(Guid.Empty, "AAPL", Market.UnitedStates).Should().BeNull("空の起点からは導かない");
    }

    // T-10-2244: 形は RFC 9562 の版 8・変種 10（下流の既存の Guid の扱いを変えない）。名前空間を変えると値が変わることも固定する
    // （値の固定: 既知の入力に対する既知の出力。名前空間・名前の組み立てを変えると、稼働中のサイクルの再配送で ID がずれる）。
    [Fact]
    public void T_10_2244_DecisionIdは版8の名前ベースで既知の入力に既知の値を返す()
    {
        var id = ScheduledDecisionIds.For(
            new Guid("0b7f6f8e-3f1c-4d3a-9a51-2f0f1f5d9c11"), "AAPL", Market.UnitedStates)!.Value;
        var text = id.ToString("D");

        text[14].Should().Be('8', "版 8");
        "89ab".Should().Contain(text[19].ToString(), "変種 10");
        text.Should().Be(KnownId);
    }

    // 既知の入力（上の試験）に対する値。実装の名前空間・名前の組み立てを変えたら赤くなる。
    private const string KnownId = "c40c7fa3-7310-80df-a74f-98bac87c1544";
}
