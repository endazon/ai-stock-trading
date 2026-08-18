using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// ADR-0012, FR-08, #348, IADR-0171 決定1:
/// <b>本ユニットが MCP（Model Context Protocol）への公開を宣言していないこと</b>を構造で固定する。
/// <para>
/// ADR-0012（Accepted・2026-07-23）は、取引報告書・判断根拠・収集情報を基盤 MCP サーバーの
/// 公開許可リストに<b>含めない</b>と決めた。基盤側は既定非公開（許可リスト方式）であるため、
/// 「含めない」は<b>追加実装なしで成立する</b>——しかしそれは<b>登録しない限り</b>の話であり、
/// <b>登録する側は本ユニットである</b>。
/// </para>
/// <para>
/// つまりこの統制は「何もしない」ことによって守られており、<b>コード上のどこにも書かれていない</b>。
/// 次に構成を触る者から見れば、MCP 公開は「まだ作っていない機能」と区別が付かない。
/// <b>「実装していない」と「実装してはならない」は別である</b>（IADR-0164 決定1 と同じ論法）。
/// </para>
/// <para>
/// <b>検出は「MCP らしいキー名の列挙」ではなく「<c>mcp</c> の出現そのもの」で行う。</b>
/// 基盤 MCP は MSP#445 で作り直される途中であり、<b>宣言がどういう形で
/// 書かれるかは未知である</b>。キー名を列挙すると、想定と違う形で書かれた瞬間に黙って素通りする。
/// 誤検出の代償は「テストが落ちたので人が読む」だけであり、<b>本テストが求めているのはまさにそれ</b>である。
/// </para>
/// <para>
/// <b>本テストが見るのは本リポジトリ内だけである。</b> 基盤側の許可リストへ基盤側の PR で
/// 追加された場合は何も言わない——それが結合確認（platform#445 待ち・`docs/blocked-tasks.md`）が
/// 別途必要な理由である。
/// </para>
/// </summary>
public class McpExposureNotDeclaredTests
{
    /// <summary>
    /// 走査<b>しない</b>拡張子。<b>許可リストではなく拒否リストで書く。</b>
    /// <para>
    /// 当初は走査する拡張子の許可リスト（`.cs` `.json` `.yaml` …）で書いていたが、
    /// <b>許可リストそのものが「暗黙の除外」として働いていた</b>——`Dockerfile` は
    /// <c>Path.GetExtension</c> が空文字列を返すため、<b>2 件が黙って走査から漏れていた</b>
    /// （`backend/Dockerfile` / `deploy/opend/Dockerfile`。PR #451 のレビュー指摘）。
    /// Dockerfile は <c>RUN</c> / <c>ENV</c> で MCP サイドカーやクライアントの導入を書き得る場所であり、
    /// <b>粗い照合の強さ（決定2）を部分的に無効化していた</b>。
    /// </para>
    /// <para>
    /// 拒否リストにすれば、<b>新しい種類のファイルは既定で走査対象になる</b>。
    /// 除外は<b>意図的に 1 種類だけ</b>——Markdown は設計文書であり、
    /// 「MCP へは公開しない」と<b>書くこと自体が正しい</b>場所である（本 ADR・仕様書がそうである）。
    /// </para>
    /// </summary>
    private static readonly string[] NotScannedExtensions = [".md"];

    /// <summary>
    /// 走査対象から外すファイル。<b>本テスト自身のみ</b>（説明のために <c>mcp</c> を含む）。
    /// <b>ここが育ったぶんだけ検査は弱くなる</b>——追加するときは IADR-0171 に理由を残すこと。
    /// </summary>
    private static readonly string[] AllowedFiles = ["McpExposureNotDeclaredTests.cs"];

    /// <summary>
    /// 走査対象ファイル数の下限。<b>0 件走査でも「違反 0 件」で緑になる</b>ため、
    /// 対象が痩せていないことを明示的に固定する（着手時点の実測は 1100 件超）。
    /// </summary>
    private const int MinimumScannedFiles = 900;

    [Fact]
    public void MCP公開の宣言がbackendとdeployのどこにも存在しない()
    {
        var violations = ScannedFiles()
            .Where(p => ContainsMcpToken(File.ReadAllText(p)))
            .Select(p => Path.GetRelativePath(RepositoryLayout.Root, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        violations.Should().BeEmpty(
            "ADR-0012 は取引報告書・判断根拠・収集情報を MCP 公開許可リストに含めないと決めている"
                + "（基盤は既定非公開であり、登録する側が本ユニットである）。"
                + " 公開が必要になった場合は ADR-0012 を Superseded する新 ADR で、対象文書・ABAC 属性・"
                + "データ越境ティア判定・無人エージェントの権限スコープを定めてから行うこと"
                + "（docs/security/security.md「MCP（外部 AI エージェント）への公開」節）。"
                + " 該当ファイル: {0}",
            string.Join(", ", violations));
    }

    // 対照（肯定形）その 1: 走査対象が痩せていないこと。
    // これが無いと、対象の絞り込みを誤って 0 件になったときにも上のテストは緑になる。
    [Fact]
    public void 走査対象が十分な数のファイルを含んでいる()
    {
        ScannedFiles().Should().HaveCountGreaterThan(
            MinimumScannedFiles,
            "走査対象が痩せると「違反 0 件」が「1 件も読んでいない」と区別できなくなる");
    }

    // 対照（肯定形）その 2: **拡張子を持たない構成ファイルが走査対象に入っていること。**
    // `Dockerfile` は `Path.GetExtension` が空文字列を返すため、拡張子の許可リストで書くと
    // **黙って漏れる**（PR #451 のレビューで実際に漏れていた）。RUN / ENV で MCP サイドカーや
    // クライアントの導入を書き得る場所であり、**漏れていることは失敗メッセージにも現れない**。
    [Fact]
    public void 拡張子を持たない構成ファイルも走査対象に入っている()
    {
        var scanned = ScannedFiles()
            .Select(p => Path.GetRelativePath(RepositoryLayout.Root, p).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);

        scanned.Should().Contain("backend/Dockerfile");
        scanned.Should().Contain("deploy/opend/Dockerfile");
    }

    // 対照（肯定形）その 3: **Markdown は意図的に走査しない。**
    // これは唯一の意図的な除外であり、「うっかり外れている」のか「外してある」のかを
    // テストで区別できるようにしておく（本 ADR・仕様書が `mcp` を多数含むため必要な除外である）。
    [Fact]
    public void Markdownは意図的に走査対象から外れている()
    {
        ScannedFiles().Should().NotContain(
            p => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase),
            "設計文書は「MCP へは公開しない」と書くこと自体が正しい場所である");
    }

    // 対照（肯定形）その 4: 照合器そのものが効くこと。
    // 実ツリーが 0 件であるため、照合器が常に false を返すよう壊れても上のテストは緑のままである。
    [Theory]
    [InlineData("mcp")]
    [InlineData("MCP")]
    [InlineData("\"mcpServers\": {}")]
    [InlineData("McpExposureOptions")]
    [InlineData("  mcp-server:\n    enabled: true")]
    [InlineData("Exposure:Mcp:Enabled")]
    public void 照合器はMCPの記述を実際に検出する(string text)
    {
        ContainsMcpToken(text).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("public sealed record TradeDecision(string Symbol);")]
    [InlineData("Compression: enabled")]
    public void 照合器は無関係な記述を検出しない(string text)
    {
        ContainsMcpToken(text).Should().BeFalse();
    }

    /// <summary>
    /// <b>大文字小文字を問わない部分一致</b>。着手時点の実測で <c>backend/</c> と <c>deploy/</c> には
    /// <c>mcp</c> という並びが<b>1 件も存在しない</b>（部分一致・大文字小文字無視で 0 件）。
    /// <b>0 件だからこそ、最も粗い照合が最も強い。</b>
    /// </summary>
    private static bool ContainsMcpToken(string text) =>
        text.Contains("mcp", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> ScannedFiles() =>
        new[] { "backend", "deploy" }
            .Select(d => Path.Combine(RepositoryLayout.Root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            .Where(IsScanned)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

    private static bool IsScanned(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.Contains("/bin/", StringComparison.Ordinal)
            || normalized.Contains("/obj/", StringComparison.Ordinal)
            || normalized.Contains("/node_modules/", StringComparison.Ordinal))
        {
            return false;
        }

        if (AllowedFiles.Contains(Path.GetFileName(path), StringComparer.Ordinal)) return false;

        return !NotScannedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }
}
