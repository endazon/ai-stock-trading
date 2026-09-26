using RiskManagementService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, ADR-0040 決定1, #826 項目 2, IADR-0344 決定7, IADR-0347（T-10-1576）: リスク管理の損切りライン到達の検知ログは、
// 建玉ごとの手法を知らないため断定せず S0〜S3 の 4 手法の帰結を列挙し、リスク管理は発注しないことを書く。
// 列挙から手法が落ちると、その手法の建玉の運用者がログから帰結を読めない（#826 項目 2 の残余で S3 が落ちていた）。
public class StopLossTriggeredDetectionLogTests
{
    [Fact]
    public void 検知ログは4手法の帰結を列挙しリスク管理は発注しないと書く()
    {
        var logger = new CapturingLogger();
        var handler = new StopLossTriggeredHandler(logger);

        handler.Handle(new StopLossTriggered(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, 10, 950m, 940m, DateTimeOffset.UnixEpoch));

        var entry = logger.Entries.Should().ContainSingle().Which;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain("AAPL").And.Contain("リスク管理は発注しない")
            .And.Contain("S0=ブローカー側の逆指値")
            .And.Contain("S1=発注執行が成行決済")
            .And.Contain("S2=誰も決済しない")
            .And.Contain("S3=ブローカー側の代替注文");
    }

    private sealed class CapturingLogger : ILogger<StopLossTriggeredHandler>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
