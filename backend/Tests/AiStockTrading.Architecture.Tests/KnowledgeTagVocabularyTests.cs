using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// FR-01, FR-08, IADR-0315, #705: <c>AiStockTrading.Shared.KnowledgeBase.KnowledgeTagVocabulary</c>
/// （AST が KB へ送り得るタグの静的語彙の単一情報源）が、複製元の各実体と食い違っていないことを固定する。
/// <para>
/// <c>KnowledgeTagVocabulary</c> は Shared プロジェクトに置かれるため、値の複製元
/// （<c>InformationCollectionService.Domain.InformationKind</c> / <c>SourceAllowlist.Default</c> /
/// <c>ReportService.Domain.ReportKind</c> と <c>ReportKnowledgeMapper</c> の <c>"report"</c>）を
/// 型として参照できない（依存方向は Services → Shared。IADR-0256）。本テストは
/// <c>RetrievalSourceVocabularyTests</c> と同じ作法（ソースの静的解析）で一致を検査する。
/// </para>
/// </summary>
public class KnowledgeTagVocabularyTests
{
    private static readonly string VocabularyPath = Path.Combine(
        RepositoryLayout.Root, "backend", "Shared", "AiStockTrading.Shared.KnowledgeBase",
        "KnowledgeTagVocabulary.cs");

    private static readonly string InformationKindPath = Path.Combine(
        RepositoryLayout.Root, "backend", "Services", "InformationCollectionService",
        "Domain", "CollectedInformation.cs");

    private static readonly string SourceAllowlistPath = Path.Combine(
        RepositoryLayout.Root, "backend", "Services", "InformationCollectionService",
        "Domain", "SourceAllowlist.cs");

    private static readonly string TradingReportPath = Path.Combine(
        RepositoryLayout.Root, "backend", "Services", "ReportService", "Domain", "TradingReport.cs");

    private static readonly string ReportKnowledgeMapperPath = Path.Combine(
        RepositoryLayout.Root, "backend", "Services", "ReportService", "Infrastructure",
        "ExternalServices", "ReportKnowledgeMapper.cs");

    [Fact]
    public void CollectionKindsはInformationKind列挙体の全値と一致する()
    {
        var vocabulary = QuotedLiteralsInField(VocabularyPath, "CollectionKinds");
        var actual = EnumMemberNames(InformationKindPath, "InformationKind");

        vocabulary.Should().NotBeEmpty("KnowledgeTagVocabulary.CollectionKinds を読めないなら本検査は無意味である");
        actual.Should().NotBeEmpty("InformationKind の列挙値を読めないなら本検査は無意味である");

        vocabulary.Should().BeEquivalentTo(actual,
            "KnowledgeTagVocabulary.CollectionKinds は InformationKind の全値の複製である。" +
            "InformationKind に値を足したら KnowledgeTagVocabulary.CollectionKinds と docs/operations/ の Runbook も追随すること");
    }

    [Fact]
    public void CollectionSourcesはSourceAllowlistDefaultと一致する()
    {
        var vocabulary = QuotedLiteralsInField(VocabularyPath, "CollectionSources");
        var actual = QuotedLiteralsInField(SourceAllowlistPath, "Default");

        vocabulary.Should().NotBeEmpty("KnowledgeTagVocabulary.CollectionSources を読めないなら本検査は無意味である");
        actual.Should().NotBeEmpty("SourceAllowlist.Default を読めないなら本検査は無意味である");

        vocabulary.Should().BeEquivalentTo(actual,
            "KnowledgeTagVocabulary.CollectionSources は SourceAllowlist.Default の複製である。" +
            "収集ソースを追加・削除したら両方（と RetrievalSourcePolicy.Default・docs/operations/ の Runbook）を追随すること");
    }

    [Fact]
    public void ReportTagsはreportとReportKind列挙体の全値の小文字形の和集合と一致する()
    {
        var vocabulary = QuotedLiteralsInField(VocabularyPath, "ReportTags");
        var reportKinds = EnumMemberNames(TradingReportPath, "ReportKind")
            .Select(name => name.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var expected = reportKinds.Concat(["report"]).ToHashSet(StringComparer.Ordinal);

        vocabulary.Should().NotBeEmpty("KnowledgeTagVocabulary.ReportTags を読めないなら本検査は無意味である");
        reportKinds.Should().NotBeEmpty("ReportKind の列挙値を読めないなら本検査は無意味である");

        vocabulary.Should().BeEquivalentTo(expected,
            "KnowledgeTagVocabulary.ReportTags は \"report\" と ReportKind の全値（小文字化）の和集合である。" +
            "ReportKind に値を足したら KnowledgeTagVocabulary.ReportTags と docs/operations/ の Runbook も追随すること");
    }

    // ReportKnowledgeMapper が実際に付与する固定タグ "report" が、リファクタで消えていないことを見る
    // （素朴な部分文字列検査。RetrievalSourceVocabularyTests と同じ「形が変わったら気付ける」作法）。
    [Fact]
    public void ReportKnowledgeMapperは固定タグreportを付与している()
    {
        File.Exists(ReportKnowledgeMapperPath).Should().BeTrue($"ReportKnowledgeMapper を読めない: {ReportKnowledgeMapperPath}");
        var text = File.ReadAllText(ReportKnowledgeMapperPath);

        text.Should().Contain("\"report\"",
            "ReportKnowledgeMapper が付ける固定タグ \"report\" が見つからない。KnowledgeTagVocabulary.ReportTags の前提が崩れている");
    }

    [Fact]
    public void All語彙は3群の和集合であり漏れがない()
    {
        var kinds = QuotedLiteralsInField(VocabularyPath, "CollectionKinds");
        var sources = QuotedLiteralsInField(VocabularyPath, "CollectionSources");
        var reportTags = QuotedLiteralsInField(VocabularyPath, "ReportTags");
        var expected = kinds.Concat(sources).Concat(reportTags).ToHashSet(StringComparer.Ordinal);

        File.Exists(VocabularyPath).Should().BeTrue();
        var text = File.ReadAllText(VocabularyPath);
        var allStart = text.IndexOf("All ", StringComparison.Ordinal);
        allStart.Should().BeGreaterThan(-1, "KnowledgeTagVocabulary.All が見つからない");

        // All は 3 フィールドの Concat であることをソース上で確認する（値そのものは実行時に決まるため、
        // ここでは「3 群すべてを合成している」ことをテキストで確認するに留める）。
        var allBody = text[allStart..];
        allBody.Should().Contain("CollectionKinds");
        allBody.Should().Contain("CollectionSources");
        allBody.Should().Contain("ReportTags");

        expected.Should().NotBeEmpty();
    }

    /// <summary>
    /// 指定フィールド名の出現位置から、初期化子の終端（<c>];</c> または <c>});</c>。どちらの記法
    /// （コレクション式 <c>[...]</c> ／ <c>new(new[] {...})</c>）にも対応する）までに並ぶ二重引用符
    /// リテラルを拾う。<c>RetrievalSourceVocabularyTests.QuotedLiteralsInDefault</c> と同じ作法——
    /// 解析は素朴でよい。形が変わって拾えなくなれば <c>NotBeEmpty</c> が落ちて気付ける
    /// （黙って 0 件検査へ落ちない）。
    /// </summary>
    private static IReadOnlyCollection<string> QuotedLiteralsInField(string path, string fieldName)
    {
        File.Exists(path).Should().BeTrue($"読めない: {path}");
        var text = File.ReadAllText(path);

        var start = text.IndexOf(fieldName, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"フィールド `{fieldName}` が見つからない: {path}");

        var end = text.IndexOf("];", start, StringComparison.Ordinal);
        if (end < 0)
            end = text.IndexOf("});", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, $"`{fieldName}` の初期化子の終端が見つからない: {path}");

        return Regex.Matches(text[start..end], "\"([a-z0-9-]+)\"", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 指定 enum の本体からメンバ名を拾う（<c>//</c> コメントを除去してから抽出する。素朴な解析）。
    /// </summary>
    private static IReadOnlyCollection<string> EnumMemberNames(string path, string enumName)
    {
        File.Exists(path).Should().BeTrue($"読めない: {path}");
        var text = File.ReadAllText(path);

        var enumStart = text.IndexOf($"enum {enumName}", StringComparison.Ordinal);
        enumStart.Should().BeGreaterThan(-1, $"`enum {enumName}` が見つからない: {path}");

        var braceStart = text.IndexOf('{', enumStart);
        braceStart.Should().BeGreaterThan(-1, $"`enum {enumName}` の本体（`{{`）が見つからない: {path}");

        var braceEnd = text.IndexOf('}', braceStart);
        braceEnd.Should().BeGreaterThan(braceStart, $"`enum {enumName}` の本体の終端が見つからない: {path}");

        var body = text[(braceStart + 1)..braceEnd];
        var withoutComments = Regex.Replace(body, "//[^\n]*", string.Empty);

        return Regex.Matches(withoutComments, @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*,?\s*$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .Where(s => s.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }
}
