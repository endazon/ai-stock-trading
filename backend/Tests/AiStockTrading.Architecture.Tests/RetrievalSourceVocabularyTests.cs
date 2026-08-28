using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// FR-04, FR-08, ADR-0003, ADR-0004, #252, IADR-0169 決定2:
/// <b>収集側の許可ソース語彙と、RAG 注入側の許可出所語彙が一致していること</b>を機械的に固定する。
/// <para>
/// 判断サービスは収集サービスを参照しない（ユニット境界）。したがって語彙は<b>2 か所に書かれる</b>——
/// 片方を増やしてもう片方を忘れると、<b>新しい収集ソースの文書が KB に入るのに判断へは一度も届かない</b>
/// （しかも「文脈なし」で正常動作しているように見えるため気付けない）。
/// </para>
/// <para>
/// 本テストは<b>ソースの静的解析</b>で行う（`RepositoryLayout` と同じ作法）。被検査プロジェクトを
/// <c>ProjectReference</c> すると、アーキテクチャテスト自身がユニット境界を跨いでしまう。
/// </para>
/// </summary>
public class RetrievalSourceVocabularyTests
{
    private static readonly string CollectionAllowlistPath = Path.Combine(
        RepositoryLayout.Root, "backend", "Services", "InformationCollectionService", "src",
        "InformationCollectionService.Domain", "SourceAllowlist.cs");

    private static readonly string RetrievalPolicyPath = Path.Combine(
        RepositoryLayout.Root, "backend", "Services", "TradeDecisionService", "src",
        "TradeDecisionService.Application", "Services", "RetrievalSourcePolicy.cs");

    [Fact]
    public void 収集側の許可ソースはすべてRAG注入側でも許可されている()
    {
        var collectionSources = QuotedLiteralsInDefault(CollectionAllowlistPath);
        var retrievalTags = QuotedLiteralsInDefault(RetrievalPolicyPath);

        collectionSources.Should().NotBeEmpty("収集側の許可リストを読めていないなら本検査は無意味である");
        retrievalTags.Should().NotBeEmpty("注入側の許可リストを読めていないなら本検査は無意味である");

        var missing = collectionSources.Except(retrievalTags, StringComparer.OrdinalIgnoreCase).ToArray();
        missing.Should().BeEmpty(
            "収集側で受理したソースの文書が KB に入るのに判断へ一度も届かない状態になる"
                + "（しかも『文脈なし』で正常に見えるため気付けない）。RetrievalSourcePolicy.Default へ追加すること。"
                + "欠けているソース: {0}",
            string.Join(", ", missing));
    }

    // 注入側は自リポジトリの文書種別（report）を**上乗せ**して持つ。逆向きの完全一致は要求しない
    // ——ただし「収集ソースでも自リポ文書種別でもないもの」が紛れ込んでいないことは見る。
    [Fact]
    public void RAG注入側の許可出所は収集ソースか自リポジトリ文書種別のいずれかである()
    {
        var collectionSources = QuotedLiteralsInDefault(CollectionAllowlistPath);
        var retrievalTags = QuotedLiteralsInDefault(RetrievalPolicyPath);

        // 自リポジトリが書く文書のタグ（ReportKnowledgeMapper が付ける）。増やすときは意図的に。
        // #336, ADR-0020 決定2-1: collection-status は収集サービス自身が書く「欠測の明示」であり、
        // 外部から取得したテキストではない（DegradationNotice.SourceName）。
        string[] ownDocumentKinds = ["report", "collection-status"];

        var unexpected = retrievalTags
            .Except(collectionSources, StringComparer.OrdinalIgnoreCase)
            .Except(ownDocumentKinds, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        unexpected.Should().BeEmpty(
            "許可出所は『ADR-0004 が受理を決めた収集ソース』か『自リポジトリが書いた文書』のいずれかであること。"
                + "新しい信頼の定義を発明していないか確認すること。想定外: {0}",
            string.Join(", ", unexpected));
    }

    /// <summary>
    /// `Default` の初期化子に並ぶ二重引用符リテラルを拾う。
    /// <b>解析は素朴でよい</b>——どちらのファイルも「文字列リテラルを列挙するだけ」の形であり、
    /// 形が変わって拾えなくなれば <c>NotBeEmpty</c> が落ちて気付ける（黙って 0 件にならない）。
    /// </summary>
    private static IReadOnlyCollection<string> QuotedLiteralsInDefault(string path)
    {
        File.Exists(path).Should().BeTrue($"許可リストの出所を読めない: {path}");

        var text = File.ReadAllText(path);
        var start = text.IndexOf("Default", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"`Default` が見つからない: {path}");

        // `Default` 以降の最初の閉じ括弧群までを対象にする（後続のメソッド本体まで読まない）。
        var end = text.IndexOf("];", start, StringComparison.Ordinal);
        if (end < 0)
            end = text.IndexOf("});", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, $"`Default` の初期化子の終端が見つからない: {path}");

        return Regex.Matches(text[start..end], "\"([a-z0-9-]+)\"", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
