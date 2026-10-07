using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// NFR-06, #1230, IADR-0509: 400 / INVALID_ARGUMENT の文言を応答へ載せる印（<c>.ClientVisible()</c>）の使い方を構造で固定する。
/// <para>
/// 印は <b>自前の送出を組み立てた直後</b>（<c>throw new …(…).ClientVisible()</c>）にだけ付ける。
/// 捕まえた例外（第三者・CoreLib 由来）へ後から付けると、その文言が利用者へ漏れる —— IADR-0509 が
/// 残余リスクに書いた「書かれた規約だけが止めている」状態を、ここで機械の検査に置き換える（#1243 監査 🟡）。
/// </para>
/// <para>
/// 併せて、判定が<b>スタックを読まない</b>ことも固定する。旧判定（IADR-0503 決定 2）はスタックの先頭フレームで
/// 決めていたため JIT のインライン化で揺れた。判定へスタックの読み取りが戻ると同じ揺れが戻る。
/// </para>
/// </summary>
public class ClientVisibleMarkerUsageTests
{
    private static readonly Regex MarkerCall = new(@"\.ClientVisible\(\)", RegexOptions.Compiled);
    private static readonly Regex ThrowNew = new(@"\bthrow\s+new\b", RegexOptions.Compiled);
    private static readonly Regex StackReading = new(@"\b(StackTrace|StackFrame|EnhancedStackTrace)\b", RegexOptions.Compiled);

    /// <summary>印の定義そのもの（標準の検証補助を包んで印を付け直す）。ここだけは捕まえた例外へ印を付けてよい。</summary>
    private const string MarkerDefinitionFile = "ClientVisibleArgument.cs";

    [Fact]
    public void 印の付け所の走査が空振りしていない()
    {
        // 対（肯定形）: 走査が 0 件だと下の検査は無条件に緑になる。#1230 時点で本番コードに 21 箇所ある。
        MarkerSites().Should().HaveCountGreaterThan(
            15, "本番コードの .ClientVisible() が見つからないなら走査が壊れている");
    }

    [Fact]
    public void 印は自前の送出を組み立てた直後にだけ付ける()
    {
        // T-10-2427
        var violations = MarkerSites()
            .Where(s => Path.GetFileName(s.File) != MarkerDefinitionFile)
            .Where(s => !IsDirectlyThrownNew(s.Statement))
            .Select(s => $"{Relative(s.File)}:{s.Line}")
            .ToArray();

        violations.Should().BeEmpty(
            "印（.ClientVisible()）は throw new …(…).ClientVisible() の形でだけ付ける。"
                + "捕まえた例外へ付けると第三者の文言が応答へ載る（IADR-0509）。違反: {0}",
            string.Join(" / ", violations));
    }

    [Theory]
    [InlineData("throw new ArgumentException(\"x\", nameof(y)).ClientVisible()", true)]
    [InlineData("var m = guard ?? throw new ArgumentOutOfRangeException(\n  nameof(a), b, \"c\").ClientVisible()", true)]
    [InlineData("throw caught.ClientVisible()", false)]
    [InlineData("var e = new ArgumentException(\"x\"); throw e.ClientVisible()", false)]
    [InlineData("return ex.ClientVisible()", false)]
    public void 送出の形の判定(string statement, bool expected)
    {
        // 判定そのものの陽性・陰性対照（文の切り出しは ; { } で区切る）。
        var lastStatement = statement[(statement.LastIndexOfAny([';', '{', '}']) + 1)..];
        IsDirectlyThrownNew(lastStatement).Should().Be(expected);
    }

    [Fact]
    public void 文言を載せる判定はスタックを読まない()
    {
        // T-10-2428
        var files = ProductionSources()
            .Where(f => Path.GetFileName(f) is "ClientFacingErrors.cs" or MarkerDefinitionFile)
            .ToArray();

        files.Select(Path.GetFileName).Should().BeEquivalentTo(
            ["ClientFacingErrors.cs", MarkerDefinitionFile], "判定の 2 ファイルが見つからないなら走査が壊れている");

        var violations = files
            .Where(f => StackReading.IsMatch(CSharpSource.BlankCommentsAndLiterals(File.ReadAllText(f))))
            .Select(Relative)
            .ToArray();

        violations.Should().BeEmpty(
            "文言を載せるかの判定は明示の印だけで行う。スタックを読むと JIT のインライン化で結果が揺れる"
                + "（#1230 / IADR-0509 が IADR-0503 決定 2 を置き換えた理由）。違反: {0}",
            string.Join(" / ", violations));
    }

    /// <summary>印を含む文（直前の <c>;</c> <c>{</c> <c>}</c> から印まで）が <c>throw new</c> を含むこと。</summary>
    private static bool IsDirectlyThrownNew(string statement) => ThrowNew.IsMatch(statement);

    private static IEnumerable<(string File, int Line, string Statement)> MarkerSites()
    {
        foreach (var file in ProductionSources())
        {
            var source = CSharpSource.BlankCommentsAndLiterals(File.ReadAllText(file));
            foreach (Match m in MarkerCall.Matches(source))
            {
                var start = source.LastIndexOfAny([';', '{', '}'], m.Index) + 1;
                var line = source.AsSpan(0, m.Index).Count('\n') + 1;
                yield return (file, line, source[start..m.Index]);
            }
        }
    }

    /// <summary>本番の C# ソース（テスト・テスト用の補助・ビルド成果物を除く。判定の実装は PlatformShim にあるので含める）。</summary>
    private static IEnumerable<string> ProductionSources() =>
        Directory.EnumerateFiles(Path.Combine(RepositoryLayout.Root, "backend"), "*.cs", SearchOption.AllDirectories)
            .Where(RepositoryLayout.NotUnderBuildOutput)
            .Where(f => !IsTestSource(f))
            .OrderBy(f => f, StringComparer.Ordinal);

    private static bool IsTestSource(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Contains("/Tests/", StringComparison.Ordinal) || p.Contains(".Tests/", StringComparison.Ordinal);
    }

    private static string Relative(string path) => Path.GetRelativePath(RepositoryLayout.Root, path).Replace('\\', '/');
}
