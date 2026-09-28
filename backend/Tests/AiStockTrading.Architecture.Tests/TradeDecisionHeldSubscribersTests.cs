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
/// その集合を固定する（テスト・shim・ビルド出力は除く）。null 許容（<c>TradeDecisionHeld?</c>）と
/// using 別名（<c>using Held = ...TradeDecisionHeld;</c> で別名を引数型にする形）も数える。
/// </para>
/// <para>
/// <b>検出対象外</b>（2026-09-29 に本番ソースを grep し、いずれも実在しないことを確認した。PR #1080 監査）:
/// ジェネリックなハンドラ（<c>Handle&lt;T&gt;</c>）、saga（<c>Saga</c> 派生）、<c>[WolverineHandler]</c> 属性・
/// <c>IWolverineHandler</c> 実装による規約外の名前、基底型・インターフェイスで受ける形（<c>object</c> 等）、
/// MassTransit の <c>IConsumer&lt;T&gt;</c>（移行済み。コメントにしか現れない）。これらを導入するときは本検出器を広げること。
/// </para>
/// </summary>
public class TradeDecisionHeldSubscribersTests
{
    private const string EventTypeName = "TradeDecisionHeld";

    // 引数型の位置に来る名前（実名か別名）を差し込む。null 許容の `?` も許す。
    private static Regex HandlerMethod(IEnumerable<string> typeNames) => new(
        @"\b(?:Handle|Handles|Consume|Consumes)(?:Async)?\s*\(\s*(?:[\w.:]+\.)?(?:"
        + string.Join("|", typeNames.Select(Regex.Escape)) + @")\s*\??\s+\w+");

    private static bool Subscribes(string source)
    {
        var blanked = CSharpSource.BlankCommentsAndLiterals(source);
        var names = CSharpSource.UsingAliases(blanked)
            .Where(a => a.Value == EventTypeName)
            .Select(a => a.Key)
            .Append(EventTypeName);
        return HandlerMethod(names).IsMatch(blanked);
    }

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
            .Where(f => Subscribes(f.Source))
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
    [InlineData("public void Handle(TradeDecisionHeld? message) { }")]
    [InlineData("public void Handle(TradeDecisionHeld ? message) { }")]
    public void 検出器は購読の形を検出する(string method)
    {
        var source = $"namespace RiskManagementService.Infrastructure.Steps; public sealed class X {{ {method} }}";

        SubscriberFiles([("backend/Services/RiskManagementService/Infrastructure/Steps/X.cs", source)])
            .Should().ContainSingle();
    }

    // PR #1080 監査: using 別名で受けるハンドラも検出する（別名の名前で引数型を書く形）。
    [Theory]
    [InlineData("using Held = AiStockTrading.Shared.Contracts.Events.TradeDecisionHeld;", "public void Handle(Held message) { }")]
    [InlineData("using Held = AiStockTrading.Shared.Contracts.Events.TradeDecisionHeld;", "public Task HandleAsync(Held? message) => Task.CompletedTask;")]
    public void 検出器は別名で受ける購読を検出する(string alias, string method)
    {
        var source = $"{alias}\nnamespace RiskManagementService.Infrastructure.Steps;\npublic sealed class X {{ {method} }}";

        SubscriberFiles([("backend/Services/RiskManagementService/Infrastructure/Steps/X.cs", source)])
            .Should().ContainSingle();
    }

    [Fact]
    public void 検出器は別の型の別名を購読に数えない()
    {
        const string source = "using Held = AiStockTrading.Shared.Contracts.Events.TradeDecisionMade;\n"
            + "namespace X;\npublic sealed class Y { public void Handle(Held message) { } }";

        SubscriberFiles([("backend/Services/X/Y.cs", source)]).Should().BeEmpty();
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
