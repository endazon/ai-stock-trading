using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Application.Services;
using AiStockTrading.TradeDecision.Application.State;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.TradeDecision.Application.Tests;

// FR-02, FR-04, IADR-0023: プロンプトのトリガー種別分岐（定時/価格変動）の出力を検証する。
public class TradeDecisionPromptBuilderTests
{
    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 7, 10), "米国株の押し目買い方針");
    private static readonly SizingContext Context =
        new(100_000m, 50_000m, 20_000m, 0, 0m, TradeMode.Paper, TradingDefaults.CreateRiskLimits());

    [Fact]
    public void 価格変動トリガーは価格変動セクションを出力する()
    {
        var trigger = DecisionTrigger.FromPriceMovement(
            new PriceMovementDetected(Guid.NewGuid(), "AAPL", Market.UnitedStates, 1_040m, 1_000m, 0.04m, DateTimeOffset.UtcNow));

        var prompt = TradeDecisionPromptBuilder.Build(trigger, Policy, Context);

        prompt.Should().Contain("価格変動トリガー");
        prompt.Should().Contain("現在値");
        prompt.Should().Contain("AAPL");
        prompt.Should().NotContain("定時サイクル");
    }

    [Fact]
    public void 定時トリガーは定時セクションを出力し価格行を含まない()
    {
        var trigger = DecisionTrigger.Scheduled("7203", Market.Japan);

        var prompt = TradeDecisionPromptBuilder.Build(trigger, Policy, Context);

        prompt.Should().Contain("定時サイクル");
        prompt.Should().Contain("7203");
        // 定時セクションは価格データ行（現在値/基準値/変動率）を含まない。
        prompt.Should().NotContain("現在値");
    }

    // IADR-0039, L129: 一次スクリーニング用プロンプトは絞り込みに徹し、本判断と同じ JSON スキーマを再利用する。
    [Fact]
    public void スクリーニングプロンプトは絞り込み文言と共通JSONスキーマを出力する()
    {
        var trigger = DecisionTrigger.Scheduled("AAPL", Market.UnitedStates);

        var prompt = TradeDecisionPromptBuilder.BuildScreening(trigger, Policy, Context);

        prompt.Should().Contain("一次スクリーニング");
        prompt.Should().Contain("絞り込");
        prompt.Should().Contain("AAPL");
        prompt.Should().Contain(Policy.Summary);
        // Parser 共有のため本判断と同じ action スキーマ（Buy|Sell|Hold）を要求する。
        prompt.Should().Contain("Buy|Sell|Hold");
    }

    // FR-08, IADR-0072 決定2/3: RAG 取得文脈が非空なら本判断プロンプトに参考情報節として注入する（全量ログにも載る）。
    [Fact]
    public void RAG取得文脈があれば参考情報節を出力する()
    {
        var trigger = DecisionTrigger.Scheduled("AAPL", Market.UnitedStates);
        var retrieved = new[]
        {
            new RetrievedContext("Apple 決算メモ", "第 3 四半期は増収増益。", "kb://doc/1", 0.92d),
        };

        var prompt = TradeDecisionPromptBuilder.Build(trigger, Policy, Context, retrieved);

        prompt.Should().Contain("参考情報（ナレッジベース）");
        // ADR-0003: 参考情報は方針・制約を上書きしない旨を明記する。
        prompt.Should().Contain("上書きしません");
        prompt.Should().Contain("Apple 決算メモ");
        prompt.Should().Contain("第 3 四半期は増収増益。");
        prompt.Should().Contain("kb://doc/1");
    }

    // FR-08, IADR-0072 決定4: 取得文脈が空（既定＝現行動作）なら参考情報節を出さない。
    [Fact]
    public void RAG取得文脈が空なら参考情報節を出さない()
    {
        var trigger = DecisionTrigger.Scheduled("AAPL", Market.UnitedStates);

        var withNull = TradeDecisionPromptBuilder.Build(trigger, Policy, Context, retrieved: null);
        var withEmpty = TradeDecisionPromptBuilder.Build(trigger, Policy, Context, retrieved: []);
        var baseline = TradeDecisionPromptBuilder.Build(trigger, Policy, Context);

        withNull.Should().NotContain("参考情報（ナレッジベース）");
        withEmpty.Should().NotContain("参考情報（ナレッジベース）");
        // 既定（RAG 未設定）は実 LLM 結線（IADR-0061）と同一プロンプト＝現行動作を保つ。
        withNull.Should().Be(baseline);
        withEmpty.Should().Be(baseline);
    }

    // FR-08, IADR-0072 決定3: 参考情報の本文抜粋は上限（400 文字）で切り詰め、超過時は省略記号を付す。
    [Fact]
    public void RAG参考情報の本文抜粋は上限超で切り詰められ省略記号が付く()
    {
        var trigger = DecisionTrigger.Scheduled("AAPL", Market.UnitedStates);
        var longText = new string('あ', 500); // 上限 400 を超える
        var retrieved = new[] { new RetrievedContext("長文メモ", longText, null, 0.5d) };

        var prompt = TradeDecisionPromptBuilder.Build(trigger, Policy, Context, retrieved);

        prompt.Should().Contain("長文メモ");
        prompt.Should().Contain("…");
        // 400 文字ちょうど分は残り、501 文字目（全文）は残らない。
        prompt.Should().Contain(new string('あ', 400));
        prompt.Should().NotContain(longText);
    }

    // FR-08, IADR-0072 決定3: 上限以内の本文抜粋は切り詰めず省略記号を付けない。
    [Fact]
    public void RAG参考情報の本文抜粋は上限以内ならそのまま出力される()
    {
        var trigger = DecisionTrigger.Scheduled("AAPL", Market.UnitedStates);
        var text = new string('い', 400); // ちょうど上限
        var retrieved = new[] { new RetrievedContext("境界メモ", text, null, 0.5d) };

        var prompt = TradeDecisionPromptBuilder.Build(trigger, Policy, Context, retrieved);

        prompt.Should().Contain(text);
        prompt.Should().NotContain("…");
    }

    // FR-08, IADR-0072 決定2: 一次スクリーニングは費用統制のため RAG 文脈を含めない（据え置き）。
    [Fact]
    public void スクリーニングプロンプトはRAG文脈を含まない()
    {
        var trigger = DecisionTrigger.Scheduled("AAPL", Market.UnitedStates);

        var prompt = TradeDecisionPromptBuilder.BuildScreening(trigger, Policy, Context);

        prompt.Should().NotContain("参考情報（ナレッジベース）");
    }

    // FR-17, IADR-0076 決定5: 採算ゲート有効（includeProfitability=true）時のみ採算節と expectedProfitPerShare を出力する。
    [Fact]
    public void 採算ゲート有効なら本判断プロンプトは採算評価と想定利益フィールドを出力する()
    {
        var trigger = DecisionTrigger.Scheduled("AAPL", Market.UnitedStates);

        var prompt = TradeDecisionPromptBuilder.Build(trigger, Policy, Context, retrieved: null, includeProfitability: true);

        prompt.Should().Contain("採算評価（費用控除後の期待利益）");
        prompt.Should().Contain("expectedProfitPerShare");
    }

    // FR-17, IADR-0076 決定5: 採算ゲート無効（既定）はプロンプトに採算節・当該フィールドを出さず現行動作と一致する。
    [Fact]
    public void 採算ゲート無効の既定は採算評価節を出さず現行動作と一致する()
    {
        var trigger = DecisionTrigger.Scheduled("AAPL", Market.UnitedStates);

        var withDefault = TradeDecisionPromptBuilder.Build(trigger, Policy, Context);
        var withExplicitFalse = TradeDecisionPromptBuilder.Build(trigger, Policy, Context, retrieved: null, includeProfitability: false);

        withDefault.Should().NotContain("採算評価（費用控除後の期待利益）");
        withDefault.Should().NotContain("expectedProfitPerShare");
        withDefault.Should().Be(withExplicitFalse);
    }

    // FR-17, IADR-0076: 一次スクリーニングは費用統制のため採算評価節を含めない（本判断のみに載せる）。
    [Fact]
    public void スクリーニングプロンプトは採算評価節を含まない()
    {
        var trigger = DecisionTrigger.Scheduled("AAPL", Market.UnitedStates);

        var prompt = TradeDecisionPromptBuilder.BuildScreening(trigger, Policy, Context);

        prompt.Should().NotContain("採算評価（費用控除後の期待利益）");
        prompt.Should().NotContain("expectedProfitPerShare");
    }
}
