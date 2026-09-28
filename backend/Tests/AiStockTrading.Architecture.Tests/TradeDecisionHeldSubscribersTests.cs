using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// 🔴 UC-02, FR-03, FR-10, #1077, IADR-0452 決定2: <b>AI 判断後の見送り（<c>TradeDecisionHeld</c>）は発注の経路ではない。</b>
/// <para>
/// このイベントは Hold・統制による見送り、つまり「発注しない」と決めた判断である。購読してよいのは
/// 市場監視（急変の基準値の更新）と監査（台帳への記録）の 2 つだけであり、リスク管理・発注執行などが購読すると、
/// 見送った判断が承認・発注の経路へ流れ得る（安全側の逆）。<b>購読者を足すときは本テストと IADR を同時に改める。</b>
/// </para>
/// <para>
/// 判定は本番ソースの静的走査である。Wolverine の規約名（<c>Handle</c> / <c>Handles</c> / <c>Consume</c> /
/// <c>Consumes</c> とその <c>Async</c> 形）で、第 1 引数が <c>TradeDecisionHeld</c> のメソッドを持つファイルを集め、
/// その集合を固定する（テスト・shim・ビルド出力は除く）。
/// </para>
/// </summary>
public class TradeDecisionHeldSubscribersTests
{
    private static readonly Regex HandlerMethod = new(
        @"\b(?:Handle|Handles|Consume|Consumes)(?:Async)?\s*\(\s*(?:[\w.:]+\.)?TradeDecisionHeld\s+\w+",
        RegexOptions.Compiled);

    private static readonly string[] Expected =
    [
        "backend/Services/AuditService/Infrastructure/Steps/AuditEventHandlers.cs",
        "backend/Services/MarketMonitorService/Infrastructure/Steps/TradeDecisionHeldBaselineHandler.cs",
    ];

    internal static IReadOnlyList<string> ProductionSourceFiles() =>
        new[] { Path.Combine(RepositoryLayout.Root, "backend", "Services"), Path.Combine(RepositoryLayout.Root, "backend", "Bff") }
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(RepositoryLayout.NotUnderBuildOutput)
            .Select(p => Path.GetRelativePath(RepositoryLayout.Root, p).Replace('\\', '/'))
            .Where(p => !p.Contains("/Tests/", StringComparison.Ordinal) && !p.Contains("/tests/", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

    internal static IReadOnlyList<string> SubscriberFiles(IEnumerable<(string Path, string Source)> files) =>
        files
            .Where(f => HandlerMethod.IsMatch(CSharpSource.BlankCommentsAndLiterals(f.Source)))
            .Select(f => f.Path)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void 判断後の見送りを購読するのは市場監視の基準値と監査だけである()
    {
        var files = ProductionSourceFiles();
        files.Should().HaveCountGreaterThan(200, "走査が空振りして無条件に緑になる経路を塞ぐ");

        var subscribers = SubscriberFiles(files.Select(p => (p, File.ReadAllText(Path.Combine(RepositoryLayout.Root, p)))));

        subscribers.Should().Equal(Expected,
            "TradeDecisionHeld は発注の経路ではない（IADR-0452 決定2）。購読者を足すなら IADR と本テストを同時に改めること");
    }

    // 検出器が load-bearing であること（リスク管理に購読者を足す形を実際に検出する）の構造的証明。
    [Theory]
    [InlineData("public void Handle(TradeDecisionHeld message) { }")]
    [InlineData("public Task HandleAsync(TradeDecisionHeld message, CancellationToken ct) => Task.CompletedTask;")]
    [InlineData("public void Consume(AiStockTrading.Shared.Contracts.Events.TradeDecisionHeld m) { }")]
    public void 検出器は購読の形を検出する(string method)
    {
        var source = $"namespace RiskManagementService.Infrastructure.Steps; public sealed class X {{ {method} }}";

        SubscriberFiles([("backend/Services/RiskManagementService/Infrastructure/Steps/X.cs", source)])
            .Should().ContainSingle();
    }

    [Theory]
    [InlineData("public static AuditEntry From(TradeDecisionHeld e, Guid id) => null!;")]
    [InlineData("// public void Handle(TradeDecisionHeld message) { }")]
    [InlineData("public void Handle(TradeDecisionMade message) { }")]
    public void 検出器は購読でない形を数えない(string method)
    {
        var source = $"namespace X; public sealed class Y {{ {method} }}";

        SubscriberFiles([("backend/Services/X/Y.cs", source)]).Should().BeEmpty();
    }
}
