using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// NFR, platform ADR-0030 §基本方針, platform ADR-0019 決定 4, IADR-0256:
/// <b>Domain から到達できる共有プロジェクトが外部ライブラリへ依存しないこと</b>を csproj の静的解析で守る。
/// <para>
/// 検査 (b)(c) は Domain のソースだけを見る。Domain が
/// <c>AiStockTrading.Shared.Contracts</c> を using するのは正当であるため、
/// <b>そこへ EF Core を足せば「Domain は .NET 標準のみ」が迂回できてしまう</b>。
/// 共有プロジェクトは Vertical Slice 移行後も csproj のまま残るので、
/// <b>この経路の検査は csproj 静的解析のままでよく、そのほうが強い</b>（宣言を見るため
/// <c>global using</c> やソースジェネレータの生成参照にも影響されない）。
/// </para>
/// <para>
/// 推移閉包の探索は <c>DomainLayerDependencyTests</c> にも同じものがあるが、<b>意図して別に持つ</b>。
/// あちらは層をプロジェクトで分ける構成に依存しており移行完了後に退役させる予定であり、
/// <b>退役させた瞬間にこちらの検査まで一緒に消えてはならない</b>。
/// </para>
/// </summary>
public class SharedProjectDependencyTests
{
    /// <summary>
    /// Domain から参照してよい共有プロジェクト（＝外部ライブラリ依存ゼロを守らせる対象）。
    /// <c>AiStockTrading.Shared.Kernel</c> は<b>まだ実体が無い</b>（VSA 移行の土台 5 で新設する）。
    /// 一覧に書いておくのは、<b>新設された瞬間に検査へ入る</b>ようにするためである
    /// （新設 PR が検査の追加を忘れても規律が効く）。
    /// </summary>
    private static readonly string[] DomainReachableSharedProjects =
    [
        "AiStockTrading.Shared.Contracts",
        "AiStockTrading.Shared.Kernel",
    ];

    [Fact]
    public void Domain_から到達してよい共有プロジェクトが外部ライブラリへ依存しない()
    {
        var violations = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var root in TargetProjects())
        {
            foreach (var reached in TransitiveClosure(root.Path))
            {
                var project = ProjectFile.Load(reached);
                if (project.PackageReferences.Count > 0)
                {
                    violations.Add(
                        $"{root.Name} → … → {project.RelativePath}"
                            + $" ({string.Join(", ", project.PackageReferences)})");
                }
            }
        }

        violations.Should().BeEmpty(
            "Domain が using してよい共有プロジェクトに外部ライブラリが入ると、"
                + "「Domain は .NET 標準のみに依存する」（platform ADR-0030 §基本方針）は迂回できてしまう。"
                + "外部ライブラリが要る共有物は AiStockTrading.Shared.Infrastructure 側へ置くこと。違反: {0}",
            string.Join(" / ", violations));
    }

    // 対（肯定形）: 検査対象が 0 件になっていないこと。
    // 共有プロジェクトの改名・移動で対象を失っても、上の検査は「違反 0 件」で緑になる。
    [Fact]
    public void 共有プロジェクトの探索が空振りしていない()
    {
        RepositoryLayout.SharedProjectFiles.Should().NotBeEmpty("backend/Shared 配下の csproj 走査が壊れている");

        TargetProjects().Should().NotBeEmpty(
            "Domain から到達してよい共有プロジェクトが 1 つも見つからない。"
                + "改名されたか、走査が壊れている。実際に見つかった共有プロジェクト: {0}",
            string.Join(", ", RepositoryLayout.SharedProjectFiles.Select(Path.GetFileNameWithoutExtension)));
    }

    // 対（肯定形）: 推移閉包が「起点より先」を実際に辿っていること。
    // 閉包が起点だけを返すよう壊れても、迂回経路の検査は緑のままになる。
    [Fact]
    public void 推移閉包は参照の先まで実際に辿る()
    {
        var withReferences = RepositoryLayout.ServiceProjectFiles
            .Select(ProjectFile.Load)
            .First(p => p.ProjectReferences.Count > 0);

        TransitiveClosure(withReferences.Path).Should().HaveCountGreaterThan(
            1,
            "起点だけを返すなら、迂回経路（共有プロジェクトへ外部ライブラリを足す）は検出できない。起点: {0}",
            withReferences.RelativePath);
    }

    private static IReadOnlyList<ProjectFile> TargetProjects() =>
        RepositoryLayout.SharedProjectFiles
            .Where(p => DomainReachableSharedProjects.Contains(
                Path.GetFileNameWithoutExtension(p), StringComparer.Ordinal))
            .Select(ProjectFile.Load)
            .ToArray();

    /// <summary>起点を含む、ProjectReference で到達可能な csproj の集合。</summary>
    private static IReadOnlyCollection<string> TransitiveClosure(string rootCsproj)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(rootCsproj));

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current)) continue;
            foreach (var referenced in ProjectFile.Load(current).ProjectReferences) pending.Push(referenced);
        }

        return visited;
    }
}
