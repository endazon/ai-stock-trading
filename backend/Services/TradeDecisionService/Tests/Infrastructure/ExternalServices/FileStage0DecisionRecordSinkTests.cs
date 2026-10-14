using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradeDecisionService.Infrastructure.ExternalServices;
using Xunit;

namespace TradeDecisionService.Tests.Infrastructure.ExternalServices;

// FR-15, ADR-0033 決定5, #1342, IADR-0537 決定3: 記録集合の書き出し。書き出し先は永続ボリュームにあり、課金済みの記録が残る。
// 🔴 件数が減る上書きを拒み、書き込みは一時ファイルを経て置き換える（一時ファイルを残さない）。
public sealed class FileStage0DecisionRecordSinkTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("stage0-sink-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Path_ => Path.Combine(_root.FullName, "nested", "records.json");

    private FileStage0DecisionRecordSink Sink() => new(Path_, NullLogger<FileStage0DecisionRecordSink>.Instance);

    private static Stage0DecisionRecordSet Set(int count) =>
        new(
            new DateOnly(2026, 9, 15), new DateOnly(2026, 10, 9),
            [new Stage0RecordedSymbol("AAPL", Market.UnitedStates)],
            new DateOnly(2026, 6, 30), DateTimeOffset.UnixEpoch, "claude-sonnet-5-5", $"ai-decision-replay/x/{count}",
            [.. Enumerable.Range(0, count).Select(i => new Stage0DecisionRecord(
                "AAPL", Market.UnitedStates, new DateOnly(2026, 9, 15).AddDays(i), "fp", "claude-sonnet-5-5", 1,
                [], Stage0DecisionAction.Hold, "根拠", 0, 1m, 10, 20))]);

    private Stage0DecisionRecordSet? Read() => Stage0DecisionRecordJson.TryDeserialize(File.ReadAllText(Path_));

    [Fact]
    public async Task 書き出して読み戻せ一時ファイルを残さない()
    {
        (await Sink().SaveAsync(Set(2), TestContext.Current.CancellationToken)).Should().BeTrue();

        Read()!.Records.Should().HaveCount(2);
        Directory.GetFiles(_root.FullName, "*.tmp-*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    // 🔴 課金済みの既存の記録を、より少ない件数（0 件を含む）の集合で上書きしない。
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task 件数が減る上書きは拒み既存の記録を残す(int fewer)
    {
        var ct = TestContext.Current.CancellationToken;
        await Sink().SaveAsync(Set(3), ct);

        (await Sink().SaveAsync(Set(fewer), ct)).Should().BeFalse();

        Read()!.Records.Should().HaveCount(3);
    }

    // 同じ件数・増える件数（途中からの再開で書き出し直す・通し試験の後の本番の記録）は置き換える。
    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public async Task 同じか多い件数なら置き換える(int count)
    {
        var ct = TestContext.Current.CancellationToken;
        await Sink().SaveAsync(Set(3), ct);

        (await Sink().SaveAsync(Set(count), ct)).Should().BeTrue();

        Read()!.StrategyId.Should().Be($"ai-decision-replay/x/{count}");
    }

    // 読めない既存のファイルは守る対象にしない（記録として使えない）。
    [Fact]
    public async Task 読めない既存のファイルは置き換える()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.GetDirectoryName(Path_)!);
        await File.WriteAllTextAsync(Path_, "{not json", ct);

        (await Sink().SaveAsync(Set(1), ct)).Should().BeTrue();

        Read()!.Records.Should().ContainSingle();
    }
}
