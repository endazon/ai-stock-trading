using AiStockTrading.Shared.Contracts.Llm;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using TradeDecisionService.Domain;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-04, FR-11, ADR-0003, IADR-0039: 多数決・二段オーケストレーションの検証。
// 実 LLM 非決定性を「順次出力の fake」で再現し、票割れ・スクリーニング打ち切り・モデルルーティング・呼び出し回数を決定的に確認する。
public class DecisionOrchestratorTests
{
    // 呼び出しごとに出力を進め、(prompt, model, purpose) を記録する fake。非決定性（票の割れ）を決定的に再現する。
    private sealed class SequencedLlm(params string[] outputs) : ILlmCompletionClient
    {
        private int _index;
        public List<(string Prompt, string? Model)> Calls { get; } = [];

        // #335, IADR-0212: 層別の用途（purpose）を記録する。モデルの希望値と違い、**割当統制と費用区分はこちらで引かれる**。
        public List<string?> Purposes { get; } = [];

        public Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken ct = default)
        {
            Calls.Add((prompt, model));
            Purposes.Add(purpose);
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

        var result = await orchestrator.DecideAsync(() => "screen", "decision", signedHeldQuantity: null);

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

        var result = await Create(llm, options).DecideAsync(() => "screen", "decision", signedHeldQuantity: null);

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

        var result = await Create(llm, options).DecideAsync(() => "screen", "decision", signedHeldQuantity: null);

        result.Decision.Action.Should().Be(TradeAction.Hold);
    }

    [Fact]
    public async Task 二段_一次スクリーニングがHoldなら二次を呼ばず打ち切る()
    {
        // IADR-0039: 費用統制。一次 Hold → 二次スキップ。呼び出しは一次の 1 回のみ。
        var llm = new SequencedLlm(Json("Hold"), Json("Buy"), Json("Buy"), Json("Buy"));
        var options = DecisionOrchestrationOptions.Default with { VoteCount = 3, EnableScreening = true };

        var result = await Create(llm, options).DecideAsync(() => "screen", "decision", signedHeldQuantity: null);

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

        var result = await Create(llm, options).DecideAsync(() => "screen", "decision", signedHeldQuantity: null);

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

        var result = await Create(llm, options).DecideAsync(() => "screen", "decision", signedHeldQuantity: null);

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

        await Create(llm, options).DecideAsync(() => "screen", "decision", signedHeldQuantity: null);

        llm.Calls[0].Model.Should().Be("light"); // 一次スクリーニング
        llm.Calls.Skip(1).Should().OnlyContain(c => c.Model == "pro"); // 二次本判断
    }

    // 🔴 FR-04, ADR-0014, ADR-0017 決定1/決定2, #335, IADR-0212: **用途（purpose）も層別に渡す。**
    // モデルは希望値にすぎず、割当統制（LlmAssignments による実効モデルの照合）も費用の計上区分も
    // purpose の側で引かれる。両層が同じ用途を名乗ると、一次の応答が二次の割当と照合されて必ず割当外になる。
    [Fact]
    public async Task 二段_用途ルーティング_一次はスクリーニング用途_二次は本判断用途を渡す()
    {
        var llm = new SequencedLlm(Json("Buy"), Json("Buy"), Json("Buy"));
        var options = new DecisionOrchestrationOptions { VoteCount = 2, EnableScreening = true };

        await Create(llm, options).DecideAsync(() => "screen", "decision", signedHeldQuantity: null);

        llm.Purposes[0].Should().Be(LlmPurposes.TradeDecisionScreening);
        llm.Purposes.Skip(1).Should().OnlyContain(p => p == LlmPurposes.TradeDecision);
    }

    // 否定形: スクリーニング無効（既定）では本判断の用途しか出ない —— 一次の用途が漏れ出さないことを固定する。
    [Fact]
    public async Task スクリーニング無効なら本判断の用途しか渡さない()
    {
        var llm = new SequencedLlm(Json("Buy"));

        await Create(llm, DecisionOrchestrationOptions.Default).DecideAsync(() => "screen", "decision", signedHeldQuantity: null);

        llm.Purposes.Should().Equal(LlmPurposes.TradeDecision);
    }

    [Fact]
    public async Task スクリーニング無効なら一次プロンプトを構築しない_遅延評価()
    {
        // IADR-0039: 既定（スクリーニング無効）の経路では screeningPromptFactory を評価しない（無駄な構築を避ける）。
        var llm = new SequencedLlm(Json("Buy"));
        var factoryCalls = 0;

        await Create(llm, DecisionOrchestrationOptions.Default)
            .DecideAsync(() => { factoryCalls++; return "screen"; }, "decision", signedHeldQuantity: null);

        factoryCalls.Should().Be(0);
    }

    [Fact]
    public void VoteCountは1以上でなければならない()
    {
        var act = () => DecisionOrchestrationOptions.Default with { VoteCount = 0 };

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // --- #337（#290 吸収）, IADR-0248: 解析不能と見送りの区別 ---

    [Fact]
    public async Task 二次の解析不能票はHold票として数えつつ解析不能件数を区別して残す()
    {
        // 3 票中 1 票が解析不能（散文のみ）。Buy 2 票で Buy が勝つが、UnparseableVotes=1 が記録に残る。
        var llm = new SequencedLlm(Json("Buy"), "モデルが散文で回答した（JSON なし）", Json("Buy"));
        var orchestrator = Create(llm, DecisionOrchestrationOptions.Default with { VoteCount = 3 });

        var result = await orchestrator.DecideAsync(() => "screen", "decision", signedHeldQuantity: null);

        result.Decision.Action.Should().Be(TradeAction.Buy);
        result.UnparseableVotes.Should().Be(1, "解析不能は見送りと区別して数える（#290）");
        result.TotalVotes.Should().Be(3);
    }

    [Fact]
    public async Task 一次スクリーニングの解析不能は見送りと区別して打ち切る()
    {
        var llm = new SequencedLlm("応答が JSON ではない");
        var orchestrator = Create(llm, DecisionOrchestrationOptions.Default with { EnableScreening = true });

        var result = await orchestrator.DecideAsync(() => "screen", "decision", signedHeldQuantity: null);

        result.ScreenedOut.Should().BeTrue("解析不能でも安全側で打ち切る（挙動は従来どおり）");
        result.ScreeningUnparseable.Should().BeTrue("打ち切りの理由が解析不能であることを区別して残す（#290）");
        llm.Calls.Should().HaveCount(1, "二次は呼ばない");
    }

    [Fact]
    public async Task 一次スクリーニングの見送りは解析不能として記録しない_対の肯定形()
    {
        var llm = new SequencedLlm("""{"action":"Hold","rationale":"方針外"}""");
        var orchestrator = Create(llm, DecisionOrchestrationOptions.Default with { EnableScreening = true });

        var result = await orchestrator.DecideAsync(() => "screen", "decision", signedHeldQuantity: null);

        result.ScreenedOut.Should().BeTrue();
        result.ScreeningUnparseable.Should().BeFalse("LLM が選んだ見送りは解析不能ではない");
        result.Decision.Rationale.Should().Be("方針外");
    }

    [Fact]
    public async Task 全票解析可能なら解析不能件数は0_対の肯定形()
    {
        var llm = new SequencedLlm(Json("Buy"));
        var orchestrator = Create(llm, DecisionOrchestrationOptions.Default with { VoteCount = 2 });

        var result = await orchestrator.DecideAsync(() => "screen", "decision", signedHeldQuantity: null);

        result.UnparseableVotes.Should().Be(0);
        result.ScreeningUnparseable.Should().BeFalse();
    }

    // --- FR-04, FR-11, #806, IADR-0248: 一次スクリーニングは方向だけを読む ---

    // #806: 一次で意味を持つのは関心の方向だけ。Buy/Sell の数値欠損・不変量違反（本判断なら InvalidValues）でも
    // 見送りにせず二次本判断へ進める（2026-09-16 開場中の実測: Buy＋stopLossDistancePerShare=null で
    // screenedOut=True / screeningUnparseable=True になり本判断へ届かなかった）。
    [Theory]
    [InlineData("""{"action":"Buy","rationale":"上昇","referencePrice":332.55,"stopLossDistancePerShare":null}""")]
    [InlineData("""{"action":"Buy","rationale":"上昇","referencePrice":1000,"stopLossDistancePerShare":1000}""")]
    [InlineData("""{"action":"Sell","rationale":"反落"}""")]
    public async Task 一次のBuySellは数値が無くても不変量違反でも二次へ進む(string screeningOutput)
    {
        var llm = new SequencedLlm(screeningOutput, Json("Buy"), Json("Buy"), Json("Buy"));
        var options = DecisionOrchestrationOptions.Default with { VoteCount = 3, EnableScreening = true };

        var result = await Create(llm, options).DecideAsync(() => "screen", "decision", signedHeldQuantity: null);

        result.ScreenedOut.Should().BeFalse("一次の関心ありは数値の有無に関わらず二次へ進む");
        result.ScreeningUnparseable.Should().BeFalse("数値欠損は出力の形の問題ではない");
        result.Decision.Action.Should().Be(TradeAction.Buy, "二次本判断が価格・損切り幅を決める");
        result.TotalVotes.Should().Be(3);
        llm.Calls.Should().HaveCount(4);
        llm.Calls[0].Prompt.Should().Be("screen");
        llm.Calls.Skip(1).Should().OnlyContain(c => c.Prompt == "decision");
    }

    // #806 陰性対照: Hold は数値 null でも見送り（解析不能ではない・根拠を保つ）。二次は呼ばない。
    [Fact]
    public async Task 一次のHoldは数値がnullでも見送りとして打ち切り解析不能にしない()
    {
        var llm = new SequencedLlm(
            """{"action":"Hold","rationale":"方針外","referencePrice":null,"stopLossDistancePerShare":null}""", Json("Buy"));
        var options = DecisionOrchestrationOptions.Default with { EnableScreening = true };

        var result = await Create(llm, options).DecideAsync(() => "screen", "decision", signedHeldQuantity: null);

        result.ScreenedOut.Should().BeTrue();
        result.ScreeningUnparseable.Should().BeFalse("LLM が選んだ見送りは解析不能ではない");
        result.Decision.Action.Should().Be(TradeAction.Hold);
        result.Decision.Rationale.Should().Be("方針外");
        result.TotalVotes.Should().Be(0);
        llm.Calls.Should().ContainSingle();
    }

    // #806 陰性対照: 壊れた JSON・不明な action は引き続き解析不能として打ち切る（#290 の区別を維持）。
    [Theory]
    [InlineData("""{"action":"Buy",}""")]
    [InlineData("""{"action":"Maybe","rationale":"x"}""")]
    public async Task 一次の壊れたJSONや不明なactionは解析不能として打ち切る(string screeningOutput)
    {
        var llm = new SequencedLlm(screeningOutput, Json("Buy"));
        var options = DecisionOrchestrationOptions.Default with { EnableScreening = true };

        var result = await Create(llm, options).DecideAsync(() => "screen", "decision", signedHeldQuantity: null);

        result.ScreenedOut.Should().BeTrue("解析不能でも安全側で打ち切る");
        result.ScreeningUnparseable.Should().BeTrue("打ち切りの理由が解析不能であることを区別して残す（#290）");
        result.Decision.Action.Should().Be(TradeAction.Hold);
        result.TotalVotes.Should().Be(0);
        llm.Calls.Should().ContainSingle("二次は呼ばない");
    }

    // --- FR-04, FR-11, #1187, IADR-0248: 解析不能のログに action を載せ、保有の文脈で決済の損切り幅を任意にする ---

    // T-10-2286: 二次の解析不能の Warning に、解析できた action（InvalidValues なら Buy/Sell。形の問題は「不明」）と保有を載せる。
    // 従来は action が無く、「捨てたのは利確の Sell だった」が推定でしか言えなかった（PoC 2026-10-06）。detail はモデル出力（不明な action
    // の文字列）を含み得るため 1 行へ正規化する。
    [Fact]
    public async Task 二次の解析不能のログはactionを載せdetailをサニタイズする()
    {
        var llm = new SequencedLlm(
            """{"action":"Buy","rationale":"押し目","referencePrice":255.55,"stopLossDistancePerShare":null}""",
            "{\"action\":\"Ma\\nybe\",\"rationale\":\"x\"}");
        var logger = new CapturingLogger();
        var orchestrator = new DecisionOrchestrator(llm, DecisionOrchestrationOptions.Default with { VoteCount = 2 }, logger);

        var result = await orchestrator.DecideAsync(() => "screen", "decision", signedHeldQuantity: 970);

        result.UnparseableVotes.Should().Be(2);
        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Should().HaveCount(2);

        // 1 票目: ロング保有中の Buy（買い増し＝新規建て）で損切り幅なし → InvalidValues。action=Buy が載る。
        warnings[0].Values["Kind"].Should().Be(TradeDecisionParseFailureKind.InvalidValues);
        warnings[0].Values["Action"].Should().Be("Buy");
        warnings[0].Values["Held"].Should().Be("970");
        warnings[0].Message.Should().Contain("action=Buy").And.Contain("held=970");

        // 2 票目: 不明な action（改行入り）→ UnknownAction。action は「不明」、detail は改行を含まない。
        warnings[1].Values["Kind"].Should().Be(TradeDecisionParseFailureKind.UnknownAction);
        warnings[1].Values["Action"].Should().Be("不明");
        ((string)warnings[1].Values["Detail"]!).Should().NotContain("\n").And.Contain("Ma_ybe");
    }

    // T-10-2285（オーケストレータ）: 渡された保有で二次を読む。ロング保有中の Sell は損切り幅なしでも Sell 票、保有 0 なら解析不能票。
    [Theory]
    [InlineData(970, TradeAction.Sell, 0)]
    [InlineData(0, TradeAction.Hold, 1)]
    [InlineData(null, TradeAction.Hold, 1)]
    public async Task 二次は渡された保有で決済の損切り幅を任意にする(int? held, TradeAction expected, int unparseable)
    {
        var llm = new SequencedLlm("""{"action":"Sell","rationale":"利確","referencePrice":255.55,"stopLossDistancePerShare":null}""");

        var result = await Create(llm, DecisionOrchestrationOptions.Default).DecideAsync(() => "screen", "decision", held);

        result.Decision.Action.Should().Be(expected);
        result.UnparseableVotes.Should().Be(unparseable);
    }

    // 🔴 T-10-2515（#1290, IADR-0524 決定 2/3）: 一次の根拠文に文字化けの疑いがあれば、受け取った地点で 1 回だけ検出して
    // Warning を 1 行出し、印を運ぶ。見送りなら根拠に目印を前置する（原文は残す）。🔴 action は変えない（関心ありなら本判断へ進む）。
    [Theory]
    [InlineData("Hold")]
    [InlineData("Buy")]
    public async Task T_10_2515_一次の根拠文の化けは警告と印を出しactionは変えない(string screeningAction)
    {
        const string garbled = "監視銘HeaderItemに含まれるが材料なし";
        var llm = new SequencedLlm(
            $$"""{"action":"{{screeningAction}}","rationale":"{{garbled}}"}""",
            Json("Buy"));
        var logger = new CapturingLogger();
        var orchestrator = new DecisionOrchestrator(
            llm, DecisionOrchestrationOptions.Default with { EnableScreening = true }, logger);

        var result = await orchestrator.DecideAsync(() => "screen", "decision", signedHeldQuantity: null);

        result.ScreeningRationaleGarbleSuspected.Should().BeTrue();
        var warning = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Which;
        warning.Message.Should().Contain("文字化けの疑い").And.Contain(garbled);
        if (screeningAction == "Hold")
        {
            result.ScreenedOut.Should().BeTrue();
            result.Decision.Action.Should().Be(TradeAction.Hold);
            result.Decision.Rationale.Should().Be($"{RationaleGarbleDetector.Marker}: {garbled}");
            llm.Calls.Should().ContainSingle("見送りは本判断を呼ばない（従来どおり）");
        }
        else
        {
            result.ScreenedOut.Should().BeFalse("化けを理由に Hold へ倒さない");
            result.Decision.Action.Should().Be(TradeAction.Buy);
            result.Decision.Rationale.Should().Be("Buy理由", "本判断の根拠文には目印を付けない（印は一次の根拠文についての記録）");
            llm.Calls.Should().HaveCount(2);
        }
    }

    // T-10-2515（否定形）: 化けの無い根拠文・解析不能・一次なしでは警告も印も目印も出ない。
    [Theory]
    [InlineData("""{"action":"Hold","rationale":"AAPL はウォッチリストに含まれるが指標RSIが過熱のため Hold"}""", true)]
    [InlineData("not json", true)]
    [InlineData("""{"action":"Hold","rationale":"x"}""", false)]
    public async Task T_10_2515_化けの無い根拠文では警告も目印も出ない(string screeningOutput, bool enableScreening)
    {
        var llm = new SequencedLlm(enableScreening ? [screeningOutput, Json("Buy")] : [Json("Buy")]);
        var logger = new CapturingLogger();
        var orchestrator = new DecisionOrchestrator(
            llm, DecisionOrchestrationOptions.Default with { EnableScreening = enableScreening }, logger);

        var result = await orchestrator.DecideAsync(() => "screen", "decision", signedHeldQuantity: null);

        result.ScreeningRationaleGarbleSuspected.Should().BeFalse();
        result.Decision.Rationale.Should().NotContain(RationaleGarbleDetector.Marker);
        logger.Entries.Should().NotContain(e => e.Message.Contains("文字化けの疑い", StringComparison.Ordinal));
    }

    private sealed record LogEntry(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Values);

    private sealed class CapturingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(p => p.Key, p => p.Value)
                : new Dictionary<string, object?>();
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), values));
        }
    }
}
