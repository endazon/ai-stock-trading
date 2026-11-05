extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-04, NFR, #1523, IADR-0607 決定1/2: プロンプトキャッシュの接頭辞は「前文＋当日の日報方針の節」だけである。
// 🔴 モデルが読む本文（接頭辞＋残り）は従前と 1 文字も変えない。接頭辞は呼び出しごとに変わる値（時刻・現在値・保有・銘柄・
// 監視銘柄）を含まない（1 バイトでも違うとキャッシュは読まれず、書きの割増だけが掛かる）。
public class PromptCachePrefixTests
{
    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 11, 5), "米国株の押し目買い方針。\n条件 A: 前日比 -2% 以下で反発の兆し。");
    private static readonly SizingContext Context =
        new(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

    private static readonly string Nl = Environment.NewLine;

    // 従前の Build / BuildScreening が先頭に書いていた文言（#1523 の変更前の字句。全文を変えないことの固定点。方針の節は #1529 で符号化した）。
    private static string ExpectedDecisionPrefix(DailyPolicy policy) =>
        "あなたは確定済み日報の方針とリスク制約の範囲内でのみ判断する取引アシスタントです。" + Nl
        + "方針の範囲外・不確実な場合は必ず Hold（取引しない）を選びます。" + Nl
        + Nl
        + PolicyBlock(policy);

    private static string ExpectedScreeningPrefix(DailyPolicy policy) =>
        "あなたは取引候補の一次スクリーニング担当です。詳細な本判断は行いません。" + Nl
        + "確定済み日報の方針に照らし、この銘柄が本判断に値する取引候補かを絞り込みます。" + Nl
        + "方針の範囲外・関心なし・不確実な場合は必ず Hold（見送り）を選びます。" + Nl
        + Nl
        + PolicyBlock(policy);

    // 🔴 FR-04, ADR-0003, #1529, IADR-0618: 方針の節は本文を 1 行 1 件の JSON 文字列でフェンスと境界語の中に置く（#1529 で字句が変わった）。
    // 試験の方針は引用符・バックスラッシュ・制御文字を含まないので、各行は引用符で囲むだけで JSON 文字列になる。
    private static string PolicyBlock(DailyPolicy policy) =>
        $"# 確定済み日報の方針（{policy.Date:yyyy-MM-dd}）" + Nl
        + TradeDecisionPromptBuilder.PolicyBoundaryNotice + Nl
        + "```json" + Nl
        + "<<<UNTRUSTED_DATA" + Nl
        + string.Concat(policy.Summary.Split('\n').Select(line => "\"" + line + "\"" + Nl))
        + "UNTRUSTED_DATA>>>" + Nl
        + "```json" + Nl
        + Nl;

    // 同じ日の 2 回の呼び出し（銘柄・市場・時刻・現在値・保有・監視銘柄・トリガーの種別がすべて違う）。
    private static (string Decision, string Screening) CallA()
    {
        var trigger = DecisionTrigger.FromPriceMovement(
            new PriceMovementDetected(Guid.NewGuid(), "AAPL", Market.UnitedStates, 1_040m, 1_000m, 0.04m,
                new DateTimeOffset(2026, 11, 5, 14, 31, 0, TimeSpan.Zero)));
        var clock = new MarketClockContext(Market.UnitedStates, new DateTimeOffset(2026, 11, 5, 14, 31, 0, TimeSpan.Zero), null);
        IReadOnlyList<WatchedSymbol> watchlist = [new("AAPL", Market.UnitedStates), new("MSFT", Market.UnitedStates)];
        return (
            TradeDecisionPromptBuilder.Build(trigger, Policy, Context, currentPrice: 1_040m, held: HeldPosition.None,
                watchlist: watchlist, marketClock: clock),
            TradeDecisionPromptBuilder.BuildScreening(trigger, Policy, Context, currentPrice: 1_040m, held: HeldPosition.None,
                watchlist: watchlist, marketClock: clock));
    }

    private static (string Decision, string Screening) CallB()
    {
        var trigger = DecisionTrigger.Scheduled("MSFT", Market.UnitedStates);
        var clock = new MarketClockContext(Market.UnitedStates, new DateTimeOffset(2026, 11, 5, 15, 2, 17, TimeSpan.Zero), null);
        IReadOnlyList<WatchedSymbol> watchlist = [new("MSFT", Market.UnitedStates)];
        var held = new HeldPosition(10, 412.5m, 400m);
        return (
            TradeDecisionPromptBuilder.Build(trigger, Policy, Context, currentPrice: 415.25m, held: held,
                watchlist: watchlist, marketClock: clock),
            TradeDecisionPromptBuilder.BuildScreening(trigger, Policy, Context, currentPrice: 415.25m, held: held,
                watchlist: watchlist, marketClock: clock));
    }

    [Fact]
    public void 接頭辞は従前のプロンプトの先頭の字句と同じである()
    {
        TradeDecisionPromptBuilder.DecisionCacheablePrefix(Policy).Should().Be(ExpectedDecisionPrefix(Policy));
        TradeDecisionPromptBuilder.ScreeningCacheablePrefix(Policy).Should().Be(ExpectedScreeningPrefix(Policy));
    }

    [Fact]
    public void 接頭辞と残りを足すと組んだプロンプトの全文と一致する()
    {
        foreach (var (decision, screening) in new[] { CallA(), CallB() })
        {
            var d = LlmPrompt.Split(decision, TradeDecisionPromptBuilder.DecisionCacheablePrefix(Policy));
            d.CacheablePrefix.Should().Be(ExpectedDecisionPrefix(Policy));
            (d.CacheablePrefix + d.Rest).Should().Be(decision);
            d.FullText.Should().Be(decision);

            var s = LlmPrompt.Split(screening, TradeDecisionPromptBuilder.ScreeningCacheablePrefix(Policy));
            s.CacheablePrefix.Should().Be(ExpectedScreeningPrefix(Policy));
            (s.CacheablePrefix + s.Rest).Should().Be(screening);
            s.FullText.Should().Be(screening);
        }
    }

    [Fact]
    public void 同じ日の2回の呼び出しで接頭辞は1バイトも違わず残りは違う()
    {
        var a = CallA();
        var b = CallB();
        var prefixD = TradeDecisionPromptBuilder.DecisionCacheablePrefix(Policy);
        var prefixS = TradeDecisionPromptBuilder.ScreeningCacheablePrefix(Policy);

        var aD = LlmPrompt.Split(a.Decision, prefixD);
        var bD = LlmPrompt.Split(b.Decision, prefixD);
        System.Text.Encoding.UTF8.GetBytes(aD.CacheablePrefix!).Should().Equal(System.Text.Encoding.UTF8.GetBytes(bD.CacheablePrefix!));
        aD.Rest.Should().NotBe(bD.Rest);

        var aS = LlmPrompt.Split(a.Screening, prefixS);
        var bS = LlmPrompt.Split(b.Screening, prefixS);
        System.Text.Encoding.UTF8.GetBytes(aS.CacheablePrefix!).Should().Equal(System.Text.Encoding.UTF8.GetBytes(bS.CacheablePrefix!));
        aS.Rest.Should().NotBe(bS.Rest);
    }

    [Fact]
    public void 接頭辞は銘柄_時刻_現在値_監視銘柄を含まない()
    {
        foreach (var prefix in new[]
                 {
                     TradeDecisionPromptBuilder.DecisionCacheablePrefix(Policy),
                     TradeDecisionPromptBuilder.ScreeningCacheablePrefix(Policy),
                 })
        {
            prefix.Should().NotContain("AAPL").And.NotContain("MSFT").And.NotContain("1040").And.NotContain("415.25");
            prefix.Should().NotContain(TradeDecisionPromptBuilder.WatchlistSectionTitle);
            prefix.Should().NotContain(TradeDecisionPromptBuilder.HeldPositionSectionTitle);
            prefix.Should().NotContain("14:31").And.NotContain("15:02");
        }
    }

    [Fact]
    public void 方針の日付か本文が違えば接頭辞も違う()
    {
        var next = Policy with { Date = new DateOnly(2026, 11, 6) };
        var edited = Policy with { Summary = Policy.Summary + "追記" };

        TradeDecisionPromptBuilder.DecisionCacheablePrefix(next).Should().NotBe(TradeDecisionPromptBuilder.DecisionCacheablePrefix(Policy));
        TradeDecisionPromptBuilder.DecisionCacheablePrefix(edited).Should().NotBe(TradeDecisionPromptBuilder.DecisionCacheablePrefix(Policy));
    }

    [Fact]
    public void 接頭辞が全文の先頭でなければ接頭辞を付けずに全文を送る()
    {
        var prompt = CallA().Decision;

        var mismatched = LlmPrompt.Split(prompt, TradeDecisionPromptBuilder.ScreeningCacheablePrefix(Policy));
        mismatched.CacheablePrefix.Should().BeNull();
        mismatched.Rest.Should().Be(prompt);

        var none = LlmPrompt.Split(prompt, null);
        none.CacheablePrefix.Should().BeNull();
        none.Rest.Should().Be(prompt);

        var empty = LlmPrompt.Split(prompt, string.Empty);
        empty.CacheablePrefix.Should().BeNull();
        empty.Rest.Should().Be(prompt);

        // 接頭辞が全文と同じ長さ（残りが空）も分けない（ゲートウェイへ空の本文を送らない）。
        var whole = LlmPrompt.Split(prompt, prompt);
        whole.CacheablePrefix.Should().BeNull();
        whole.Rest.Should().Be(prompt);
    }
}
