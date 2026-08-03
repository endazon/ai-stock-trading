using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// NFR, platform ADR-0030（§決定 選定基準 3「層の依存規律」／§基本方針「Domain 層は外部ライブラリへ依存しない
/// （.NET 標準のみ）」）, IADR-0128 決定 6, issue #353:
/// Domain 層の依存規律を csproj の静的解析で機械的に強制する。
///
/// レビューの記憶に頼ると、EF Core の属性を 1 つ使いたくなった瞬間に規律は失われる。
/// 「なぜ落ちたか」がその場で分かるよう、違反はプロジェクト名と参照名を添えて報告する。
/// </summary>
public class DomainLayerDependencyTests
{
    /// <summary>
    /// Domain が <c>ProjectReference</c> してよい相手。ファイル名（拡張子なし）で判定する。
    /// - <c>*.Domain</c>: 他サービスの Domain（現行 4 件の既知の状態。作業仕様書 §5.9 / §12 未決事項 5）
    /// - <c>*.SharedKernel</c>: ADR-0030 の SharedKernel（Result / Error）。現時点では実体が無い（IADR-0128 決定 2）
    /// - <c>AiStockTrading.Shared.Contracts</c>: ユニット単位の契約プロジェクト（platform ADR-0019 決定 4）
    /// </summary>
    private static bool IsAllowedDomainDependency(string projectName) =>
        projectName.EndsWith(".Domain", StringComparison.Ordinal)
        || projectName.EndsWith(".SharedKernel", StringComparison.Ordinal)
        || projectName == "AiStockTrading.Shared.Contracts";

    // 検査4（先に置く）: 探索そのものが壊れていないこと。
    // Domain プロジェクトが 0 件になると、以下の検査はすべて「違反なし」で無条件に緑になる。
    // 検査器が静かに失効する経路を塞ぐためのメタ検査である（IADR-0127 と同じ性質）。
    [Fact]
    public void Domain_プロジェクトの探索が空振りしていない()
    {
        var domains = RepositoryLayout.DomainProjectFiles;

        domains.Should().HaveCountGreaterThanOrEqualTo(
            9,
            "Domain プロジェクトは実測 9 件（Backtest / Configuration / CostControl / InformationCollection / "
                + "MarketMonitor / OrderExecution / Report / RiskManagement / TradeDecision）ある。"
                + "これを下回るなら探索が壊れているか、Domain が削除された。実際に見つかったのは: {0}",
            string.Join(", ", domains.Select(Path.GetFileNameWithoutExtension)));
    }

    // 検査1: Domain は外部ライブラリへ依存しない（.NET 標準のみ）。
    // platform ADR-0030 §基本方針。NuGet パッケージが 1 つでも入れば違反。
    [Fact]
    public void Domain_は外部ライブラリへ依存しない()
    {
        var violations = RepositoryLayout.DomainProjectFiles
            .Select(ProjectFile.Load)
            .Where(p => p.PackageReferences.Count > 0)
            .Select(p => $"{p.RelativePath} → {string.Join(", ", p.PackageReferences)}")
            .ToArray();

        violations.Should().BeEmpty(
            "Domain 層は .NET 標準のみに依存する（platform ADR-0030 §基本方針）。"
                + "外部ライブラリが要る処理は Application / Infrastructure へ置く。違反: {0}",
            string.Join(" / ", violations));
    }

    // 検査2: Domain のプロジェクト参照は許可リスト内のみ。
    // Application / Infrastructure / Api / Client への逆流（依存の向きの反転）を止める。
    [Fact]
    public void Domain_のプロジェクト参照は_Domain_と_SharedKernel_と共有契約に限る()
    {
        var violations = new List<string>();
        foreach (var project in RepositoryLayout.DomainProjectFiles.Select(ProjectFile.Load))
        {
            foreach (var referenced in project.ProjectReferences)
            {
                var name = Path.GetFileNameWithoutExtension(referenced);
                if (!IsAllowedDomainDependency(name)) violations.Add($"{project.RelativePath} → {name}");
            }
        }

        violations.Should().BeEmpty(
            "Domain が参照してよいのは他の Domain・SharedKernel・AiStockTrading.Shared.Contracts のみである"
                + "（IADR-0128 決定 6）。Application / Infrastructure / Api への参照は依存の向きの反転である。違反: {0}",
            string.Join(" / ", violations));
    }

    // 検査3: 推移閉包まで外部ライブラリ依存ゼロ。
    // 検査1 だけでは「Shared.Contracts へ EF Core を足す」形の迂回を素通りさせる。
    // Domain の依存規律は「Domain の csproj に何が書いてあるか」ではなく
    // 「Domain から到達できる範囲に何があるか」で決まる。
    [Fact]
    public void Domain_から到達する全プロジェクトが外部ライブラリへ依存しない()
    {
        var violations = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var root in RepositoryLayout.DomainProjectFiles)
        {
            foreach (var reached in TransitiveClosure(root))
            {
                var project = ProjectFile.Load(reached);
                if (project.PackageReferences.Count > 0)
                {
                    violations.Add(
                        $"{Path.GetFileNameWithoutExtension(root)} → … → {project.RelativePath}"
                            + $" ({string.Join(", ", project.PackageReferences)})");
                }
            }
        }

        violations.Should().BeEmpty(
            "Domain から推移的に到達するプロジェクトも .NET 標準のみに依存していなければ、"
                + "Domain の外部依存ゼロは迂回できてしまう。違反: {0}",
            string.Join(" / ", violations));
    }

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
