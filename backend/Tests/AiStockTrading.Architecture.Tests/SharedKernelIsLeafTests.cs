using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// NFR, IADR-0260:
/// <b><c>AiStockTrading.Shared.Kernel</c> は依存グラフの葉である</b>ことを固定する。
/// <para>
/// 共有カーネルは「サービスを跨いで共有する Domain 型」の置き場であり、
/// <b>どのサービスも参照してはならない</b>。1 本でも参照が入ると、共有カーネルを引く全サービスが
/// そのサービスを間接的に引くことになり、<b>本 ADR が解消したはずの Domain 跨ぎ参照が
/// 共有カーネル経由で復活する</b>——しかも、それは
/// <c>DomainSourceDependencyTests</c> の <c>using</c> 走査には現れない
/// （各サービスの Domain は <c>AiStockTrading.Shared.Kernel</c> しか書かないため）。
/// </para>
/// <para>
/// 既存の <c>SharedProjectDependencyTests</c> は<b>外部ライブラリ依存ゼロ</b>しか見ておらず、
/// <c>Shared.Kernel</c> → <c>SomeService.Domain</c> のような <c>ProjectReference</c> を止められない
/// （参照先の Domain も <c>PackageReference</c> を持たないため、推移閉包の検査は緑のまま通る）。
/// <b>依存の向きは、外部ライブラリの有無とは別の関心事である。</b>
/// </para>
/// </summary>
public class SharedKernelIsLeafTests
{
    private const string SharedKernelProjectName = "AiStockTrading.Shared.Kernel";

    /// <summary>共有カーネルが参照してよい相手（ファイル名・拡張子なし）。</summary>
    private static readonly string[] AllowedReferences = ["AiStockTrading.Shared.Contracts"];

    // 対（肯定形・先に置く）: 検査対象が実在すること。
    // プロジェクトが改名・移動されると、以下の検査は「対象なし」で無条件に緑になる。
    [Fact]
    public void 共有カーネルのプロジェクトが実在する()
    {
        SharedKernelProject().Should().NotBeNull(
            "AiStockTrading.Shared.Kernel が backend/Shared 配下に見つからない。"
                + "改名されたか、走査が壊れている。実際に見つかった共有プロジェクト: {0}",
            string.Join(", ", RepositoryLayout.SharedProjectFiles.Select(Path.GetFileNameWithoutExtension)));
    }

    [Fact]
    public void 共有カーネルはサービスを参照しない()
    {
        var project = SharedKernelProject()!;

        var violations = project.ProjectReferences
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !AllowedReferences.Contains(name, StringComparer.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        violations.Should().BeEmpty(
            "共有カーネルはサービスを跨いで共有する Domain 型の置き場であり、依存グラフの葉でなければならない。"
                + "サービスを参照すると、共有カーネルを引く全サービスがそのサービスを間接的に引く"
                + "（＝Domain 跨ぎ参照の復活）。参照してよいのは {0} だけである。違反: {1}",
            string.Join(", ", AllowedReferences),
            string.Join(" / ", violations));
    }

    // 否定形: 許可判定そのものが load-bearing であること。
    // 実ツリーの違反は 0 件であるため、判定が常に「違反なし」を返すよう壊れても上の検査は緑のままになる。
    [Theory]
    [InlineData("RiskManagementService.Domain")]
    [InlineData("ConfigurationService.Domain")]
    [InlineData("AiStockTrading.Shared.Infrastructure")]
    [InlineData("AiStockTrading.Shared.KnowledgeBase")]
    public void 許可判定はサービスや実装側の共有物を許さない(string projectName)
    {
        AllowedReferences.Contains(projectName, StringComparer.Ordinal).Should().BeFalse();
    }

    [Fact]
    public void 許可判定は共有契約を許す()
    {
        AllowedReferences.Contains("AiStockTrading.Shared.Contracts", StringComparer.Ordinal).Should().BeTrue(
            "概算費用関数が Market・MinimumExpectedProfit を用いるため、契約プロジェクトへの参照は正当である"
                + "（契約プロジェクト自身も外部ライブラリ依存ゼロであり、Domain の依存規律を壊さない）");
    }

    private static ProjectFile? SharedKernelProject() =>
        RepositoryLayout.SharedProjectFiles
            .Where(p => Path.GetFileNameWithoutExtension(p) == SharedKernelProjectName)
            .Select(ProjectFile.Load)
            .FirstOrDefault();
}
