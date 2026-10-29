using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using TradeDecisionService.Domain;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-04, FR-11, #1332, IADR-0531 決定 1: 解析不能のときだけ、応答の原本の抜粋と形の指紋を WRN 1 行で残す
// （PoC 2026-10-12: 一次スクリーニングの NoJsonObject 2/682 で、本文が 1 文字も残らず原因を確定できなかった）。
public class DecisionOrchestratorUnparseableRawTests
{
    private sealed class SequencedLlm(params string[] outputs) : ILlmCompletionClient
    {
        private int _index;

        public int CallCount => _index;

        public Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken ct = default)
        {
            var output = outputs[Math.Min(_index, outputs.Length - 1)];
            _index++;
            return Task.FromResult(output);
        }
    }

    private const string Buy = """{"action":"Buy","rationale":"上昇","referencePrice":1000,"stopLossDistancePerShare":30}""";

    private static IEnumerable<LogEntry> RawLines(CapturingLogger logger) =>
        logger.Entries.Where(e => e.Message.StartsWith(DecisionOrchestrator.RawExcerptMarker, StringComparison.Ordinal));

    // 🔴 一次の NoJsonObject: 原本の行が 1 本出て、層・種別・候補・抜粋が読める。既存の解析不能の行と挙動（打ち切り）は変わらない。
    [Fact]
    public async Task 一次の解析不能は原本の抜粋と形の指紋を1行で残す()
    {
        const string raw = "分析の結果、関心ありと判断しました。\n以下が出力です：";
        var llm = new SequencedLlm(raw, Buy);
        var logger = new CapturingLogger();
        var orchestrator = new DecisionOrchestrator(
            llm, DecisionOrchestrationOptions.Default with { EnableScreening = true }, logger);

        var result = await orchestrator.DecideAsync(() => "screen", "decision", signedHeldQuantity: null);

        result.ScreenedOut.Should().BeTrue();
        result.ScreeningUnparseable.Should().BeTrue();
        result.Decision.Action.Should().Be(TradeAction.Hold, "挙動は従来どおり（打ち切り）");
        llm.CallCount.Should().Be(1);
        logger.Entries.Should().Contain(e => e.Message.Contains("一次スクリーニングの構造化出力が解析不能", StringComparison.Ordinal),
            "既存の行（PoC の監査が grep する）は残す");

        var line = RawLines(logger).Should().ContainSingle().Which;
        line.Level.Should().Be(LogLevel.Warning);
        line.Message.Should().Contain("layer=screening")
            .And.Contain("kind=NoJsonObject")
            .And.Contain("suspect=NoBrace")
            .And.Contain("newlines=1")
            .And.Contain("excerpt=分析の結果、関心ありと判断しました。_以下が出力です：", "改行は '_' に置き換えて 1 行に収める")
            .And.NotContain("\n");
        line.Message.Should().NotContain("screen ", "プロンプトは書かない");
    }

    // 二次本判断の解析不能票も同じ行を出す（層は decision）。解析できた票には出さない。
    [Fact]
    public async Task 二次の解析不能票も原本を残し解析できた票には出さない()
    {
        const string garbled = """{"action":"Buy","rationale":"監視銘"柄が上昇"}""";
        var llm = new SequencedLlm(garbled, Buy, Buy);
        var logger = new CapturingLogger();
        var orchestrator = new DecisionOrchestrator(llm, DecisionOrchestrationOptions.Default with { VoteCount = 3 }, logger);

        var result = await orchestrator.DecideAsync(() => "screen", "decision", signedHeldQuantity: null);

        result.UnparseableVotes.Should().Be(1);
        var line = RawLines(logger).Should().ContainSingle("解析できた 2 票には出さない").Which;
        line.Message.Should().Contain("layer=decision")
            .And.Contain("kind=NoJsonObject")
            .And.Contain("suspect=UnterminatedString")
            .And.Contain("excerpt=" + garbled);
    }

    // 🔴 肥大化を避ける: 解析できた応答（見送りを含む）と空応答（残す本文が無い）では出さない。
    [Theory]
    [InlineData("""{"action":"Hold","rationale":"方針外"}""")]
    [InlineData("   ")]
    public async Task 解析できた応答と空応答では原本を残さない(string screeningOutput)
    {
        var llm = new SequencedLlm(screeningOutput, Buy);
        var logger = new CapturingLogger();
        var orchestrator = new DecisionOrchestrator(
            llm, DecisionOrchestrationOptions.Default with { EnableScreening = true }, logger);

        await orchestrator.DecideAsync(() => "screen", "decision", signedHeldQuantity: null);

        RawLines(logger).Should().BeEmpty();
    }

    // 抽出はできたが解析できない種別（MalformedJson / UnknownAction / InvalidValues）も原本を残す（候補は NotApplicable）。
    [Theory]
    [InlineData("""{"action":"Buy",}""", "MalformedJson")]
    [InlineData("""{"action":"Maybe"}""", "UnknownAction")]
    public async Task 抽出できた解析不能も原本を残す(string screeningOutput, string kind)
    {
        var llm = new SequencedLlm(screeningOutput, Buy);
        var logger = new CapturingLogger();
        var orchestrator = new DecisionOrchestrator(
            llm, DecisionOrchestrationOptions.Default with { EnableScreening = true }, logger);

        await orchestrator.DecideAsync(() => "screen", "decision", signedHeldQuantity: null);

        RawLines(logger).Should().ContainSingle().Which.Message.Should()
            .Contain($"kind={kind}").And.Contain("suspect=NotApplicable").And.Contain("excerpt=" + screeningOutput);
    }

    // 量の上限（IADR-0531 決定 1）: 長い原本は先頭 1,000 字と末尾 500 字だけ。
    [Fact]
    public async Task 長い原本は抜粋の上限に収める()
    {
        var raw = new string('前', 5_000) + "以下が出力です：";
        var llm = new SequencedLlm(raw, Buy);
        var logger = new CapturingLogger();
        var orchestrator = new DecisionOrchestrator(
            llm, DecisionOrchestrationOptions.Default with { EnableScreening = true }, logger);

        await orchestrator.DecideAsync(() => "screen", "decision", signedHeldQuantity: null);

        var line = RawLines(logger).Should().ContainSingle().Which;
        line.Message.Should().Contain($"length={raw.Length}").And.EndWith("以下が出力です：").And.Contain("…(中略 ");
        var excerpt = (string)line.Values["Excerpt"]!;
        excerpt.Length.Should().BeLessThanOrEqualTo(
            UnparseableOutputExcerpt.HeadChars + UnparseableOutputExcerpt.TailChars + 32);
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

    // 🔴 FR-04, #1332, IADR-0597: 文字列を閉じかぎで閉じ、閉じの '"' が欠けた一次の出力は補って読み、二次本判断へ進める
    // （PoC 10/27 HOOD Sell: 解析不能で打ち切られ、決済が 6 分遅れた）。補修の WRN は事実と形だけで、本文は書かない。原本の行（解析不能）は出さない。
    [Fact]
    public async Task 一次の閉じかぎの補修は二次へ進め補修の事実と形だけをWRNで残す_1332()
    {
        const string repaired = "{\"action\":\"Sell\",\"rationale\":\"利確の水準に達した」,\"referencePrice\":1000,\"stopLossDistancePerShare\":30}";
        const string sell = """{"action":"Sell","rationale":"利確","referencePrice":1000,"stopLossDistancePerShare":30}""";
        var llm = new SequencedLlm(repaired, sell);
        var logger = new CapturingLogger();
        var orchestrator = new DecisionOrchestrator(
            llm, DecisionOrchestrationOptions.Default with { EnableScreening = true }, logger);

        var result = await orchestrator.DecideAsync(() => "screen", "decision", signedHeldQuantity: 10);

        result.ScreenedOut.Should().BeFalse("補って読めたので関心ありとして二次へ進む");
        result.ScreeningUnparseable.Should().BeFalse();
        result.Decision.Action.Should().Be(TradeAction.Sell);
        RawLines(logger).Should().BeEmpty("解析不能ではない");
        var line = logger.Entries.Should().ContainSingle(
            e => e.Message.StartsWith(DecisionOrchestrator.ClosingQuoteRepairMarker, StringComparison.Ordinal)).Which;
        line.Level.Should().Be(LogLevel.Warning);
        line.Message.Should().Contain("layer=screening").And.Contain("shape=BeforeKnownKey")
            .And.NotContain("利確の水準", "応答の本文は書かない");
    }
}
