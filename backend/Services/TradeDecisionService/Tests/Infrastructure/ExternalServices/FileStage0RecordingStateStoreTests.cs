using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
using TradeDecisionService.Infrastructure.ExternalServices;
using Xunit;

namespace TradeDecisionService.Tests.Infrastructure.ExternalServices;

// FR-15, NFR（費用）, ADR-0033 決定5, #1342, IADR-0537 決定4: Stage 0 の記録の状態をファイルへ置くアダプタ。
// 見るのは、書いたものが「再起動」（新しいインスタンス）をまたいで読めること、壊れたファイルを「無い」と読まないこと、
// ロックが排他であること、印と台帳が別のファイルであること。
public sealed class FileStage0RecordingStateStoreTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("stage0-state-");

    public void Dispose() => _root.Delete(recursive: true);

    private FileStage0RecordingStateStore Store() =>
        new(_root.FullName, TimeProvider.System, NullLogger<FileStage0RecordingStateStore>.Instance);

    private static Stage0DecisionRecordSet Checkpoint(int count) =>
        new(
            new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 16),
            [new Stage0RecordedSymbol("AAPL", Market.UnitedStates)],
            new DateOnly(2026, 6, 30), DateTimeOffset.UnixEpoch, "claude-sonnet-5-5", "ai-decision-replay/x/y",
            [.. Enumerable.Range(0, count).Select(i => new Stage0DecisionRecord(
                "AAPL", Market.UnitedStates, new DateOnly(2026, 9, 15).AddDays(i), "fp", "claude-sonnet-5-5", 1,
                [], Stage0DecisionAction.Hold, "根拠", 0, 1.5m, 10, 20))]);

    [Fact]
    public async Task 何も無ければ未開始で累計は0()
    {
        var store = Store();

        (await store.LoadAsync("run", TestContext.Current.CancellationToken)).Phase.Should().Be(Stage0RunPhase.NotStarted);
        (await store.ReadAsync("ledger", TestContext.Current.CancellationToken)).Should().Be(0m);
    }

    [Fact]
    public async Task 開始の印とチェックポイントは再起動をまたいで読め途中から再開できる()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Store().MarkStartedAsync("run", ct)).Should().BeTrue();
        (await Store().LoadAsync("run", ct)).Should().Be(new Stage0RunState(Stage0RunPhase.InProgress));

        (await Store().SaveCheckpointAsync("run", Checkpoint(2), ct)).Should().BeTrue();
        var loaded = await Store().LoadAsync("run", ct);

        loaded.Phase.Should().Be(Stage0RunPhase.InProgress);
        loaded.Checkpoint!.Records.Should().HaveCount(2);
        loaded.Checkpoint.Records[1].CostJpy.Should().Be(1.5m);
    }

    [Theory]
    [InlineData(Stage0RunPhase.Completed)]
    [InlineData(Stage0RunPhase.Stopped)]
    public async Task 終わりの印は再起動をまたいで読める(Stage0RunPhase phase)
    {
        var ct = TestContext.Current.CancellationToken;
        await Store().MarkStartedAsync("run", ct);
        (await Store().MarkFinishedAsync("run", phase, "判断 2 件", ct)).Should().BeTrue();

        var loaded = await Store().LoadAsync("run", ct);

        loaded.Phase.Should().Be(phase);
        loaded.Detail.Should().Be("判断 2 件");
    }

    // 🔴 壊れたファイルは「無い」（＝初回・累計 0）と読まない。読めないと答え、記録器は走らない。
    [Theory]
    [InlineData("runs/run.json", "{not json")]
    [InlineData("runs/run.json", """{"phase":"Bogus"}""")]
    [InlineData("runs/run.checkpoint.json", "{not json")]
    public async Task 壊れた印やチェックポイントは読めないと答える(string relative, string content)
    {
        var ct = TestContext.Current.CancellationToken;
        await Store().MarkStartedAsync("run", ct);
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, relative), content, ct);

        (await Store().LoadAsync("run", ct)).Phase.Should().Be(Stage0RunPhase.Unreadable);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"cumulativeJpy":-1}""")]
    public async Task 壊れた台帳は読めないと答える(string content)
    {
        var ct = TestContext.Current.CancellationToken;
        await Store().WriteAsync("ledger", 3m, ct);
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "ledger", "ledger.json"), content, ct);

        (await Store().ReadAsync("ledger", ct)).Should().BeNull();
    }

    // 印と台帳は別のファイル。印を消しても台帳は残る（両端で止める前提）。
    [Fact]
    public async Task 台帳は印と別のファイルで印を消しても残る()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store().MarkStartedAsync("run", ct);
        await Store().WriteAsync("ledger", 437.28m, ct);

        Directory.Delete(Path.Combine(_root.FullName, "runs"), recursive: true);

        (await Store().LoadAsync("run", ct)).Phase.Should().Be(Stage0RunPhase.NotStarted);
        (await Store().ReadAsync("ledger", ct)).Should().Be(437.28m);
    }

    [Fact]
    public void ロックは排他で外せばもう一度取れる()
    {
        var first = Store().TryAcquireRunLock();
        first.Should().NotBeNull();

        Store().TryAcquireRunLock().Should().BeNull("ほかの実行がロックを持っている");

        first!.Dispose();
        using var again = Store().TryAcquireRunLock();
        again.Should().NotBeNull();
    }

    [Fact]
    public async Task 書き込みは一時ファイルを残さない()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store().MarkStartedAsync("run", ct);
        await Store().SaveCheckpointAsync("run", Checkpoint(1), ct);
        await Store().WriteAsync("ledger", 1m, ct);
        await Store().WriteAsync("ledger", 2m, ct);

        Directory.GetFiles(_root.FullName, "*.tmp-*", SearchOption.AllDirectories).Should().BeEmpty();
        (await Store().ReadAsync("ledger", ct)).Should().Be(2m);
    }
}
