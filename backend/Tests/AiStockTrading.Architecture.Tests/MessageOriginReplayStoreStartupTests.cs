using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// 🔴 NFR, FR-10, IADR-0645 決定 2: 発行元の確かめの再送の記録の表は、起動時の移行の経路で作る（実行時には作らない）。
/// 発行元の確かめの配線（<c>UseAiStockTradingMessageOrigin</c>）と EF の移行（<c>MigrateAsync</c>）の両方を持つサービスの Program.cs は、
/// <c>MigrateAsync</c> の後に <c>EnsureMessageOriginReplayStoreAsync</c> を呼ぶこと（呼び忘れると、置き場が Database の構成で全メッセージが
/// 再試行を経てエラーキューへ流れる）。サービスの一覧は手で書かず実ツリーから得る。
/// </summary>
public class MessageOriginReplayStoreStartupTests
{
    private static readonly IReadOnlyList<string> Programs =
        Directory.GetDirectories(Path.Combine(RepositoryLayout.Root, "backend", "Services"))
            .Select(d => Path.Combine(d, "Program.cs"))
            .Where(File.Exists)
            .Where(p =>
            {
                var text = File.ReadAllText(p);
                return text.Contains("UseAiStockTradingMessageOrigin(", StringComparison.Ordinal)
                    && text.Contains("MigrateAsync()", StringComparison.Ordinal);
            })
            .ToArray();

    // 対（肯定形・先に置く）: 母集合が痩せていない（DB を持つ受け手 5 つ＋DB を持つ発行元・非受け手）。
    [Fact]
    public void 発行元の確かめとEFの移行を持つサービスの探索が空振りしていない()
    {
        Programs.Select(p => Path.GetFileName(Path.GetDirectoryName(p)!)).Should().Contain(
            ["RiskManagementService", "MarketMonitorService", "OrderExecutionService", "CostControlService", "AuditService"]);
    }

    [Fact]
    public void 移行の後に再送の記録の表を作る()
    {
        var missing = Programs
            .Where(p =>
            {
                var text = File.ReadAllText(p);
                var migrate = text.IndexOf("MigrateAsync()", StringComparison.Ordinal);
                var ensure = text.IndexOf("EnsureMessageOriginReplayStoreAsync(", StringComparison.Ordinal);
                return ensure < 0 || ensure < migrate;
            })
            .Select(p => Path.GetFileName(Path.GetDirectoryName(p)!))
            .ToList();

        missing.Should().BeEmpty("MigrateAsync の後に EnsureMessageOriginReplayStoreAsync を呼んでいない: {0}", string.Join(", ", missing));
    }
}
