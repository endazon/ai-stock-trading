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
    /// - <c>*.Domain</c>: 他サービスの Domain（<b>IADR-0260 でサービスを跨ぐ 4 本を解消し、現在は 0 本</b>。
    ///   同一サービス内の Domain 同士は将来もあり得るため許可は残す）
    /// - <c>*.SharedKernel</c>: ADR-0030 の SharedKernel（Result / Error）。<b>本リポジトリに実体は無い</b>（IADR-0128 決定 2）
    /// - <c>AiStockTrading.Shared.Contracts</c>: ユニット単位の契約プロジェクト（platform ADR-0019 決定 4）
    /// - <c>AiStockTrading.Shared.Kernel</c>: サービスを跨いで共有する Domain 型の共有カーネル（IADR-0260）。
    ///   <b>ソース走査側の許可リスト（<c>DomainSourceScan.IsAllowedDomainNamespace</c>）は先に
    ///   <c>AiStockTrading.Shared.Kernel[.*]</c> を許していたが、csproj 側は許していなかった</b>——
    ///   二重化した検査は<b>両方に同じ許可を書く</b>必要がある。
    /// </summary>
    private static bool IsAllowedDomainDependency(string projectName) =>
        projectName.EndsWith(".Domain", StringComparison.Ordinal)
        || projectName.EndsWith(".SharedKernel", StringComparison.Ordinal)
        || projectName == "AiStockTrading.Shared.Contracts"
        || projectName == "AiStockTrading.Shared.Kernel";

    // 検査4（先に置く）: 探索そのものが壊れていないこと。
    // Domain プロジェクトが 0 件になると、以下の検査はすべて「違反なし」で無条件に緑になる。
    // 検査器が静かに失効する経路を塞ぐためのメタ検査である（IADR-0127 と同じ性質）。
    //
    // NFR, IADR-0265: 下限はハードコードした数値ではなく RepositoryLayout.UnmigratedServicesWithDomainProjectCount
    // （実ツリーの src/*.Domain 走査）から動的に導く。単一プロジェクト＋VSA への移送（IADR-0259）が
    // 1 サービス進むたびに、その動的な下限自体が 1 件ずつ減る——手書きの下限を移送のたびに更新する運用は、
    // 残り 9 回のうちどこかで「2 減らすべきを 1 しか減らさない／逆に減らし過ぎる」事故を生む機会があるため、
    // 数値の更新そのものをやめた。
    [Fact]
    public void Domain_プロジェクトの探索が空振りしていない()
    {
        var domains = RepositoryLayout.DomainProjectFiles;
        var expected = RepositoryLayout.UnmigratedServicesWithDomainProjectCount;

        // 🔴 0 件になったら「探索が壊れている」のではなく「全サービスが移送済みで
        // *.Domain.csproj が 1 本も残っていない」——本検査（csproj 静的解析による層の強制）は
        // 役目を終えている。黙って「違反なし」の緑を返さず、次にすることを名指しして落とす。
        if (expected == 0)
        {
            Assert.Fail(
                "未移送で *.Domain.csproj を持つサービスが 0 件になった。全サービスが単一プロジェクト＋"
                    + "VSA へ移送済みであり、csproj 静的解析（DomainLayerDependencyTests 全体）は役目を終えた。"
                    + "ソース走査版（DomainSourceDependencyTests）へ一本化し、本クラスと "
                    + "IsAllowedDomainDependency 等の csproj 依存の仕組みを削除すること（IADR-0265 フォローアップ）。"
                    + $"実際に見つかった Domain プロジェクト: {(domains.Count == 0 ? "(なし)" : string.Join(", ", domains.Select(Path.GetFileNameWithoutExtension)))}");
            return;
        }

        domains.Should().HaveCountGreaterThanOrEqualTo(
            expected,
            "Domain プロジェクト（*.Domain.csproj）は、未移送サービスの実測 {0} 件あるはずである"
                + "（RepositoryLayout.UnmigratedServicesWithDomainProjectCount。ハードコードした数値ではなく、"
                + "実ツリーの src/*.Domain 走査から動的に導く。IADR-0265）。"
                + "単一プロジェクト＋VSA への移送（IADR-0259）が 1 サービス進むたびに、この動的な下限自体が "
                + "1 件ずつ減るのが正常であり、**減ったこと自体は退行ではない** —— 移送後の Domain フォルダは "
                + "DomainSourceDependencyTests（ソース領域を新旧の和集合で数える）が引き続き検査する。"
                + "Configuration は IADR-0264 決定 2 で Domain の唯一の型を共有カーネルへ移したため、"
                + "移送後は**どちらの検査でも数えられない**（Domain を持たないサービスになった）。"
                + "これを下回るなら探索が壊れているか、Domain が想定外に削除された。実際に見つかったのは: {1}",
            expected,
            string.Join(", ", domains.Select(Path.GetFileNameWithoutExtension)));
    }

    // ── 自己試験: 下限の導出ロジックそのものが load-bearing であること（IADR-0265）───────────
    // RepositoryLayout.UnmigratedServicesWithDomainProjectCount は実ディスクを読むため、
    // 判定の中身（CountsAsUnmigratedServiceWithDomainProject）をファイルシステムから切り離して
    // 固定する。否定形（src/ が無い・Domain 系のフォルダが無い・接尾辞が違う）を含めて確かめる ——
    // 肯定形だけでは「常に true を返す」壊れ方を検出できない。
    [Theory]
    [InlineData(true, new[] { "CostControlService.Domain" }, true)]
    [InlineData(true, new[] { "CostControlService.Application", "CostControlService.Domain" }, true)]
    // 否定形1: src/ 自体が存在しない（移送済みサービス。Domain/ はルート直下の新樹形にある）。
    [InlineData(false, new[] { "CostControlService.Domain" }, false)]
    // 否定形2: src/ はあるが Domain 系のディレクトリを持たない。
    [InlineData(true, new[] { "CostControlService.Application", "CostControlService.Infrastructure" }, false)]
    // 否定形3: src/ が空。
    [InlineData(true, new string[0], false)]
    // 否定形4: 接尾辞が ".Domain" と完全一致しない（部分一致で誤検出しない）。
    [InlineData(true, new[] { "CostControlService.DomainEvents" }, false)]
    public void 未移送判定は_src_の実在と_Domain_接尾辞のディレクトリの両方を要求する(
        bool srcDirectoryExists, string[] srcSubdirectoryNames, bool expected)
    {
        RepositoryLayout.CountsAsUnmigratedServiceWithDomainProject(srcDirectoryExists, srcSubdirectoryNames)
            .Should().Be(expected);
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
