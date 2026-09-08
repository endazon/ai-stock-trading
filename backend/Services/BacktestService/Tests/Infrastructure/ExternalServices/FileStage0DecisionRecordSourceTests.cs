using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using BacktestService.Infrastructure.ExternalServices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BacktestService.Tests.Infrastructure.ExternalServices;

// FR-04, FR-15, ADR-0033 決定2, #632, IADR-0318: 記録ファイルの読み込み。
//
// 🔴 **失敗はすべて「記録なし」へ倒す**（例外を投げない）。呼び出し元（駆動）はどの失敗でも同じ結論
// ——合格 verdict を出さない——へ進む。例外で常駐を止めると、記録の置き忘れが「サービスが落ちた」として現れる。
public class FileStage0DecisionRecordSourceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"ast-stage0-records-{Guid.NewGuid():N}");

    public FileStage0DecisionRecordSourceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static FileStage0DecisionRecordSource Source(string? path) =>
        new(path, NullLogger<FileStage0DecisionRecordSource>.Instance);

    private static Stage0DecisionRecordSet Sample() =>
        new(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30),
            [new Stage0RecordedSymbol("AAPL", Market.UnitedStates)],
            new DateOnly(2026, 3, 31), DateTimeOffset.UnixEpoch, "claude-sonnet-5", "sid",
            [new Stage0DecisionRecord("AAPL", Market.UnitedStates, new DateOnly(2026, 6, 2), "fp",
                "claude-sonnet-5", 3,
                [new Stage0RawDecision(1, Stage0DecisionAction.Buy, "根拠", 100m, 2m, 100, 20, false)],
                Stage0DecisionAction.Buy, "根拠", 10, 1m, 300, 60)]);

    // 肯定形: 書き出した記録をそのまま読み戻せる（記録側と再生側が同じ直列化を共有している証拠）。
    [Fact]
    public async Task 記録ファイルを読み戻せる()
    {
        var path = Path.Combine(_directory, "records.json");
        await File.WriteAllTextAsync(path, Stage0DecisionRecordJson.Serialize(Sample()));

        var loaded = await Source(path).LoadAsync(CancellationToken.None);

        loaded.Should().BeEquivalentTo(Sample());
    }

    // 🔴 **否定形**: パス未設定・ファイル無し・解釈不能はすべて null（＝記録なし）。
    [Fact]
    public async Task パス未設定は記録なしになる()
    {
        (await Source(null).LoadAsync(CancellationToken.None)).Should().BeNull();
        (await Source("   ").LoadAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ファイルが無ければ記録なしになる() =>
        (await Source(Path.Combine(_directory, "absent.json")).LoadAsync(CancellationToken.None))
            .Should().BeNull();

    [Fact]
    public async Task 解釈できないファイルは記録なしになる()
    {
        var path = Path.Combine(_directory, "broken.json");
        await File.WriteAllTextAsync(path, "これは JSON ではない");

        (await Source(path).LoadAsync(CancellationToken.None)).Should().BeNull();
    }
}
