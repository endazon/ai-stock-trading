using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.Llm;

// FR-04, #1295, IADR-0524（移行期間のみ・#1296 で撤去）: 割当表が直前世代を受けたら、用途とモデルの組ごとに
// プロセスあたり 1 回だけ Warning を出す。5.5 系の評価・未割当・禁止モデルでは出さない。
public class LlmPreviousGenerationWarningTests
{
    [Fact]
    public void 直前世代の受け入れは用途とモデルの組ごとに1回だけ警告する()
    {
        var gate = new LlmPreviousGenerationWarning();
        var logger = new Capture();

        Warn(gate, logger, LlmPurposes.TradeDecision, "claude-sonnet-5").Should().BeTrue();
        Warn(gate, logger, LlmPurposes.TradeDecision, "claude-sonnet-5").Should().BeFalse();
        Warn(gate, logger, LlmPurposes.ReportDaily, "claude-sonnet-5").Should().BeTrue();          // 用途が違えば別の組
        Warn(gate, logger, LlmPurposes.TradeDecisionScreening, "claude-haiku-4-5").Should().BeTrue();

        logger.Warnings.Should().HaveCount(3).And.OnlyContain(m => m.Contains(LlmPreviousGenerationWarning.Marker));
        logger.Warnings.First().Should().Contain("claude-sonnet-5").And.Contain("claude-sonnet-5-5").And.Contain("#1296");
    }

    [Theory]
    [InlineData(LlmPurposes.TradeDecision, "claude-sonnet-5-5")]   // 現行のピン
    [InlineData(LlmPurposes.ReportDaily, "claude-haiku-5-5")]      // 現行のフォールバック先
    [InlineData(LlmPurposes.TradeDecision, "claude-opus-5")]       // 他用途の直前世代＝未割当
    [InlineData(LlmPurposes.TradeDecision, "claude-fable-5-1")]    // 禁止モデル
    [InlineData(LlmPurposes.TradeDecision, null)]
    public void 直前世代でない評価では警告しない(string purpose, string? model)
    {
        var logger = new Capture();

        Warn(new LlmPreviousGenerationWarning(), logger, purpose, model).Should().BeFalse();

        logger.Warnings.Should().BeEmpty();
    }

    private static bool Warn(LlmPreviousGenerationWarning gate, ILogger logger, string purpose, string? model) =>
        gate.WarnOnce(logger, purpose, LlmAssignmentEvaluator.Evaluate(purpose, model));

    private sealed class Capture : ILogger
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings => _warnings;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                _warnings.Add(formatter(state, exception));
        }
    }
}
