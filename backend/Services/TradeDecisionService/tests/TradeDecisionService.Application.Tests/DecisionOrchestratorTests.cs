using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Application.Services;
using AiStockTrading.TradeDecision.Domain;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.TradeDecision.Application.Tests;

// FR-04, FR-11, ADR-0003, IADR-0039: 多数決・二段オーケストレーションの検証。
// 実 LLM 非決定性を「順次出力の fake」で再現し、票割れ・スクリーニング打ち切り・モデルルーティング・呼び出し回数を決定的に確認する。
public class DecisionOrchestratorTests
{
    // 呼び出しごとに出力を進め、(prompt, model) を記録する fake。非決定性（票の割れ）を決定的に再現する。
    private sealed class SequencedLlm(params string[] outputs) : ILlmCompletionClient
    {
        private int _index;
        public List<(string Prompt, string? Model)> Calls { get; } = [];

        public Task<string> CompleteAsync(string prompt, string? model = null, CancellationToken ct = default)
        {
            Calls.Add((prompt, model));
            var output = outputs[Math.Min(_index, outputs.Length - 1)];
            _index++;
            return Task.FromResult(output);
        }
    }

    private static string Json(string action, decimal price = 1_000m, decimal stop = 30m) =>
        $$"""{"action":"{{action}}","rationale":"{{action}}理由","referencePrice":{{price}},"stopLossDistancePerShare":{{stop}}}""";

    private static DecisionOrchestrator Create(ILlmCompletionClient llm, DecisionOrchestrationOptions options) =>
        new(llm, options, NullLogger<DecisionOrchestrator>.Instance);

    [Fact]
    public async Task 既定は単発判断と等価_1回だけ二次プロンプトを呼ぶ()
    {
        var llm = new SequencedLlm(Json("Buy"));
        var orchestrator = Create(llm, DecisionOrchestrationOptions.Default);

        var result = await orchestrator.DecideAsync(() => "screen", "decision");

        result.Decision.Action.Should().Be(TradeAction.Buy);
        result.ScreenedOut.Should().BeFalse();
        result.TotalVotes.Should().Be(1);
        llm.Calls.Should().ContainSingle();
        llm.Calls[0].Prompt.Should().Be("decision");
    }

    [Fact]
    public async Task 多数決_二次をVoteCount回呼び最多得票を採る()
    {
        // 3 回実行で Buy,Buy,Sell → 多数決 Buy。
        var llm = new SequencedLlm(Json("Buy"), Json("Buy"), Json("Sell"));
        var options = DecisionOrchestrationOptions.Default with { VoteCount = 3 };

        var result = await Create(llm, options).DecideAsync(() => "screen", "decision");

        result.Decision.Action.Should().Be(TradeAction.Buy);
        result.TotalVotes.Should().Be(3);
        result.AgreementVotes.Should().Be(2);
        llm.Calls.Should().HaveCount(3);
        llm.Calls.Should().OnlyContain(c => c.Prompt == "decision");
    }

    [Fact]
    public async Task 多数決_票が割れてタイなら安全側Hold()
    {
        // Buy,Sell の 2 票 → タイ → Hold。
        var llm = new SequencedLlm(Json("Buy"), Json("Sell"));
        var options = DecisionOrchestrationOptions.Default with { VoteCount = 2 };

        var result = await Create(llm, options).DecideAsync(() => "screen", "decision");

        result.Decision.Action.Should().Be(TradeAction.Hold);
    }

    [Fact]
    public async Task 二段_一次スクリーニングがHoldなら二次を呼ばず打ち切る()
    {
        // IADR-0039: 費用統制。一次 Hold → 二次スキップ。呼び出しは一次の 1 回のみ。
        var llm = new SequencedLlm(Json("Hold"), Json("Buy"), Json("Buy"), Json("Buy"));
        var options = DecisionOrchestrationOptions.Default with { VoteCount = 3, EnableScreening = true };

        var result = await Create(llm, options).DecideAsync(() => "screen", "decision");

        result.Decision.Action.Should().Be(TradeAction.Hold);
        result.ScreenedOut.Should().BeTrue();
        result.TotalVotes.Should().Be(0);
        llm.Calls.Should().ContainSingle();
        llm.Calls[0].Prompt.Should().Be("screen");
    }

    // #247, IADR-0104 決定6: 一次で打ち切る場合も見送りの根拠（LLM 由来・拒否等）を保つ。
    // Hold は TradeDecisionMade を発行しないため、FR-11 ログが唯一の監査記録である。
    [Fact]
    public async Task 二段_一次で打ち切る場合も見送りの根拠を保つ()
    {
        var refused = """{"action":"Hold","rationale":"LLM が要求を拒否したため見送り"}""";
        var llm = new SequencedLlm(refused, Json("Buy"));
        var options = DecisionOrchestrationOptions.Default with { VoteCount = 3, EnableScreening = true };

        var result = await Create(llm, options).DecideAsync(() => "screen", "decision");

        result.Decision.Action.Should().Be(TradeAction.Hold);
        result.Decision.Rationale.Should().Be("LLM が要求を拒否したため見送り");
        result.ScreenedOut.Should().BeTrue();
        result.TotalVotes.Should().Be(0);
        llm.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task 二段_一次が通過なら二次を多数決で実行する()
    {
        // 一次 Buy（通過）→ 二次 3 回（Buy,Buy,Sell）→ 多数決 Buy。合計 4 回。
        var llm = new SequencedLlm(Json("Buy"), Json("Buy"), Json("Buy"), Json("Sell"));
        var options = DecisionOrchestrationOptions.Default with { VoteCount = 3, EnableScreening = true };

        var result = await Create(llm, options).DecideAsync(() => "screen", "decision");

        result.Decision.Action.Should().Be(TradeAction.Buy);
        result.ScreenedOut.Should().BeFalse();
        result.TotalVotes.Should().Be(3);
        llm.Calls.Should().HaveCount(4);
        llm.Calls[0].Prompt.Should().Be("screen");
        llm.Calls.Skip(1).Should().OnlyContain(c => c.Prompt == "decision");
    }

    [Fact]
    public async Task 二段_モデルルーティング_一次は軽量_二次は高性能を渡す()
    {
        // IADR-0039, L34: モデル選択はポート引数でゲートウェイへ渡す。一次=PrimaryModel、二次=SecondaryModel。
        var llm = new SequencedLlm(Json("Buy"), Json("Buy"), Json("Buy"));
        var options = new DecisionOrchestrationOptions
        {
            VoteCount = 2,
            EnableScreening = true,
            PrimaryModel = "light",
            SecondaryModel = "pro",
        };

        await Create(llm, options).DecideAsync(() => "screen", "decision");

        llm.Calls[0].Model.Should().Be("light"); // 一次スクリーニング
        llm.Calls.Skip(1).Should().OnlyContain(c => c.Model == "pro"); // 二次本判断
    }

    [Fact]
    public async Task スクリーニング無効なら一次プロンプトを構築しない_遅延評価()
    {
        // IADR-0039: 既定（スクリーニング無効）の経路では screeningPromptFactory を評価しない（無駄な構築を避ける）。
        var llm = new SequencedLlm(Json("Buy"));
        var factoryCalls = 0;

        await Create(llm, DecisionOrchestrationOptions.Default)
            .DecideAsync(() => { factoryCalls++; return "screen"; }, "decision");

        factoryCalls.Should().Be(0);
    }

    [Fact]
    public void VoteCountは1以上でなければならない()
    {
        var act = () => DecisionOrchestrationOptions.Default with { VoteCount = 0 };

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
