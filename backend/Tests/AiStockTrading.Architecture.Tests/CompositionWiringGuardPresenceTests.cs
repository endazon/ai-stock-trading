using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// NFR, #947, IADR-0397: <b>すべてのサービスが組み立てガードを持つ</b>ことを表明する。
/// <para>
/// 組み立てガード（<c>AiStockTrading.TestSupport.Composition</c>）はサービスごとのテストに置く（<c>Program</c> が
/// サービスごとに別の型で、1 つのテストプロジェクトから 12 個の本番の組み立てを組めないため）。したがって
/// <b>新しいサービスはガードを持たずに増え得る</b> —— ガードの無いサービスでは「配線が消えても全テストが緑」が
/// そのまま戻る。サービスの一覧は手で書かず、実ツリー（<c>backend/Services/*/Tests/*.Tests.csproj</c>）から得る。
/// </para>
/// </summary>
public partial class CompositionWiringGuardPresenceTests
{
    private static readonly IReadOnlyList<string> ServiceTestProjects =
        RepositoryLayout.ServiceProjectFiles
            .Where(p => Path.GetFileName(p).EndsWith(".Tests.csproj", StringComparison.Ordinal))
            .ToArray();

    // 対（肯定形・先に置く）: 母集合が痩せていないこと。0 件なら下の検査は無条件に緑になる。
    [Fact]
    public void サービスのテストプロジェクトの探索が空振りしていない()
    {
        ServiceTestProjects.Should().HaveCountGreaterThanOrEqualTo(
            12,
            "backend/Services 配下のサービスは 2026-09-25 実測で 12 本（11 サービス＋OpenD 認証ゲートウェイ）ある。見つかったのは: {0}",
            string.Join(", ", ServiceTestProjects.Select(Path.GetFileNameWithoutExtension)));
    }

    [Fact]
    public void すべてのサービスのテストが本番の組み立てを検査するガードを持つ()
    {
        var missing = ServiceTestProjects
            .Where(project => !HasGuard(Path.GetDirectoryName(project)!))
            .Select(Path.GetFileNameWithoutExtension)
            .ToList();

        missing.Should().BeEmpty(
            "各サービスのテストは InspectComposition（本番の Program.cs を組む）と AssertNoUnexpectedFindings（判定）を "
                + "両方呼ぶガードを持たなければならない（IADR-0397。雛形は既存サービスの CompositionWiringGuardTests.cs）。"
                + "持たないサービス: {0}",
            string.Join(", ", missing));
    }

    // 呼び出し（識別子＋開き括弧）で数える。コメント中の言及は行コメントを落としてから数える。
    private static bool HasGuard(string testDirectory)
    {
        var sources = Directory.EnumerateFiles(testDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(RepositoryLayout.NotUnderBuildOutput)
            .Select(f => StripLineComments(File.ReadAllText(f)));
        var text = string.Join('\n', sources);
        return InspectCall().IsMatch(text) && AssertCall().IsMatch(text);
    }

    private static string StripLineComments(string source) =>
        string.Join('\n', source.Split('\n').Select(line =>
        {
            var i = line.IndexOf("//", StringComparison.Ordinal);
            return i < 0 ? line : line[..i];
        }));

    [GeneratedRegex(@"\.InspectComposition\s*\(")]
    private static partial Regex InspectCall();

    [GeneratedRegex(@"\.AssertNoUnexpectedFindings\s*\(")]
    private static partial Regex AssertCall();
}
