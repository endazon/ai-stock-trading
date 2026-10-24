using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// 🔴 T-10-3071, NFR, FR-10, #1473, IADR-0587 決定3: <b>すべてのサービスの DbContext が DateTimeOffset を DB の境界で UTC へ揃える規約を持つ</b>ことを表明する。
/// <para>
/// Npgsql は timestamptz へ UTC 以外のオフセットを書けない。市場の取引日の境界（米国東部・JST）から作った値が照会や書き込みへ渡ると、
/// 単体試験（InMemory の store・UTC の時計）では緑のまま稼働でだけ例外になる（#1473 の判断由来の決済の未約定取消）。
/// 呼び出し側を 1 つずつ直す方式では新しい照会で同じ型が戻るので、DbContext の <c>ConfigureConventions</c> で全列に変換を掛け、
/// <b>新しい DbContext がこれを持たずに増えること</b>をここで止める。DbContext の一覧は手で書かず、実ツリーのソースから得る。
/// </para>
/// </summary>
public partial class DbContextUtcDateTimeOffsetConventionTests
{
    private static readonly IReadOnlyList<string> DbContextSources =
        Directory.EnumerateFiles(Path.Combine(RepositoryLayout.Root, "backend", "Services"), "*.cs", SearchOption.AllDirectories)
            .Where(RepositoryLayout.NotUnderBuildOutput)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => DbContextDeclaration().IsMatch(File.ReadAllText(f)))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

    // 対（肯定形・先に置く）: 母集合が痩せていないこと。0 件なら下の検査は無条件に緑になる。
    [Fact]
    public void DbContextの探索が空振りしていない()
    {
        DbContextSources.Should().HaveCountGreaterThanOrEqualTo(
            7,
            "2026-10-24 実測で DbContext は 7 本（監査・設定・費用統制・市場監視・発注執行・報告書・リスク管理）ある。見つかったのは: {0}",
            string.Join(", ", DbContextSources.Select(Path.GetFileNameWithoutExtension)));
    }

    [Fact]
    public void すべてのDbContextがDateTimeOffsetをUTCへ揃える規約を持つ()
    {
        var missing = DbContextSources
            .Where(f => !Convention().IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(RepositoryLayout.Root, f))
            .ToList();

        missing.Should().BeEmpty(
            "DbContext は ConfigureConventions で Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>() を掛けること"
                + "（IADR-0587。Npgsql は timestamptz へ UTC 以外のオフセットを書けない）。持たない DbContext: {0}",
            string.Join(", ", missing));
    }

    [GeneratedRegex(@":\s*DbContext\s*\(")]
    private static partial Regex DbContextDeclaration();

    [GeneratedRegex(@"Properties<DateTimeOffset>\(\)\s*\.HaveConversion<UtcDateTimeOffsetConverter>\(\)")]
    private static partial Regex Convention();
}
