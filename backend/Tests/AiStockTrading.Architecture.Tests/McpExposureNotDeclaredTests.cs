using System.Text.Json;
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

/// <summary>
/// ADR-0012, #500, IADR-0273: <b>基盤側の公開許可リストに本ユニットのサービスが載っていないこと</b>を検査する。
/// <para>
/// 上の <see cref="McpExposureNotDeclaredTests"/> は<b>本リポジトリ内しか見ない</b>——それは
/// IADR-0171 §結果 が「悪い影響」として自ら記録した限界であり、
/// <b>基盤側の PR で本ユニットのサービスが公開対象へ追加されると、本リポジトリの CI は緑のまま統制が破れる</b>。
/// 本クラスがその欠落を埋める。
/// </para>
/// <para>
/// 見るのは基盤の宣言的公開構成
/// （<c>microservices-platform</c> の <c>src/platform/backend/Services/McpServer/Configuration/mcp-publication.json</c>）
/// である。基盤の <c>ToolCatalog.Refresh</c> は<b>この構成を起点に</b>各サービスの自己申告を探すため、
/// <b>構成に載っていないツールは、申告があっても公開されない</b>（既定非公開・許可リスト方式）。
/// したがって「本ユニットが公開されているか」は、この 1 ファイルで決まる。
/// </para>
/// <para>
/// 🔴 <b>ここに載っていないことは「本ユニットの文書が MCP から読めない」ことを意味しない。</b>
/// 公開許可リストの粒度は<b>ツール</b>であり、文書コレクションでも retrieval スコープでもない。
/// 公開済みの <c>document.*</c> / <c>retrieval.*</c> は<b>基盤の共有ナレッジベース全体</b>を対象とし、
/// 本ユニットが FR-08 で保存する文書も同じ基盤へ入る。**本検査が担保するのは
/// 「本ユニット自身のツールが公開されていないこと」だけ**である（IADR-0273 §結果 の残余リスク）。
/// </para>
/// </summary>
public sealed class McpPublicationAllowlistDriftTests(ITestOutputHelper output)
{
    /// <summary>公開構成の位置を明示する環境変数。<b>指定したのにファイルが無ければ skip ではなく失敗させる</b>。</summary>
    public const string PathOverrideVariable = "MSP_MCP_PUBLICATION_PATH";

    /// <summary>隣接クローンからの相対位置（CLAUDE.md「隣接クローン」既定・読み取り専用）。</summary>
    private static readonly string[] AdjacentClonePath =
    [
        "microservices-platform", "src", "platform", "backend", "Services", "McpServer",
        "Configuration", "mcp-publication.json"
    ];

    /// <summary>
    /// 実測（2026-09-02）の公開許可リストそのもの。<b>照合器の陰性対照</b>に使う。
    /// 実ファイルが見つからない環境（CI）でも<b>照合器だけは必ず動く</b>ようにするためであり、
    /// 「隣接クローンが無い＝何も検査しない」を作らないための対である。
    /// </summary>
    private const string PlatformPublicationSample = """
        {
          "version": "2026-08-28",
          "tools": [
            { "name": "retrieval.search_documents", "service": "retrieval-service" },
            { "name": "document.get_document", "service": "document-service" },
            { "name": "document.list_documents", "service": "document-service" },
            { "name": "graph.get_backlinks", "service": "graph-service" },
            { "name": "graph.get_links", "service": "graph-service" },
            { "name": "graph.traverse", "service": "graph-service" }
          ]
        }
        """;

    /// <summary>公開構成 1 エントリ（<c>name</c> と <c>service</c> のみ使う）。</summary>
    private sealed record PublicationEntry(string Name, string Service);

    [Fact]
    public void 基盤のMCP公開許可リストに本ユニットのサービスが載っていない()
    {
        var searched = new List<string>();
        var path = LocatePublicationConfig(searched);

        if (path is null)
        {
            // 🔴 **黙って緑にしない。** 隣接クローンは任意（本リポジトリは基盤リポジトリに依存しない）であり、
            // CI では存在しないのが正常である。しかし「見なかった」ことは結果に現れなければならない
            // —— Assert.Skip は runner 上で **Skipped（理由つき）** として報告される（Passed にならない）。
            var reason =
                "基盤の MCP 公開許可リストを読めなかったため検査を skip した"
                + $"（環境変数 {PathOverrideVariable} で明示するか、隣接クローンを置く）。"
                + " 探索した場所: " + string.Join(" / ", searched);
            output.WriteLine(reason);
            Assert.Skip(reason);
        }
        else
        {
            output.WriteLine($"公開許可リスト: {path}");
            var entries = Parse(File.ReadAllText(path));

            // 対照（肯定形）: 実ファイルを読んだのに 0 件なら、それは「違反なし」ではなく「読めていない」。
            entries.Should().NotBeEmpty(
                "公開許可リストを読んだのにツールが 0 件なら、書式が変わったか空ファイルを読んでいる"
                    + $"（{path}）。0 件走査で緑になる形にはしない");

            foreach (var e in entries) output.WriteLine($"  - {e.Service} :: {e.Name}");

            Violations(entries).Should().BeEmpty(
                "ADR-0012（Accepted）は取引報告書・判断根拠・収集情報を MCP 公開許可リストに含めないと決めている。"
                    + " 基盤側の PR で本ユニットのサービスが公開対象へ追加されると本リポジトリの CI は緑のまま統制が破れるため、"
                    + " ここで基盤の宣言的公開構成そのものを読んでいる。"
                    + " 公開が必要になった場合は ADR-0012 を Superseded する新 ADR で、対象文書・ABAC 属性・"
                    + "データ越境ティア判定・無人エージェントの権限スコープを定めてから行うこと"
                    + "（docs/security/security.md「MCP（外部 AI エージェント）への公開」節）。"
                    + " 🔴 NotificationService は基盤にも同名のサービスがあるため、"
                    + "基盤自身の notification-service が公開された場合もここで落ちる（安全側の誤検出）。"
                    + "その場合は IADR-0273 に判断を記録したうえで扱いを決めること。"
                    + " 検出: {0}",
                string.Join(", ", Violations(entries)));
        }
    }

    // 対照（肯定形）その 1: 照合器が**本ユニットのサービスを実際に検出する**こと。
    // 実データが常に 0 件であるため、照合器が常に空を返すよう壊れても上のテストは緑のままである。
    [Theory]
    [InlineData("report.get_daily_report", "report-service")]
    [InlineData("trade.get_decision_rationale", "trade-decision-service")]
    [InlineData("info.search", "information-collection-service")]
    [InlineData("x.y", "ai-stock-trading-report")]
    [InlineData("AiStockTrading.ReportService.GetDaily", "some-service")]
    public void 照合器は本ユニットのサービスの公開を検出する(string name, string service)
    {
        Violations([new PublicationEntry(name, service)]).Should().ContainSingle();
    }

    // 対照（肯定形）その 2: 照合器が**基盤側のツールを違反にしない**こと。
    // 「常に違反を返す」向きに壊れれば、上のテストは落ち続けて役に立たなくなる。
    [Fact]
    public void 照合器は基盤側の公開ツールを違反としない()
    {
        var entries = Parse(PlatformPublicationSample);
        entries.Should().HaveCount(6, "2026-09-02 の実測値。基盤側で増減したら実ファイル検査が拾う");
        Violations(entries).Should().BeEmpty();
    }

    // 対照（肯定形）その 3: 照合対象（本ユニットのサービス名）が**実ツリーから引けている**こと。
    // 手書きの一覧にすると、サービスが増えたときに黙って検査から漏れる。
    [Fact]
    public void 照合対象は実ツリーのサービスから導かれている()
    {
        AstServiceMarkers.Should().HaveCountGreaterThan(
            10, "backend/Services 配下のサービス数（2026-09-02 実測 11）から導く。痩せたら検査が弱くなる");
        AstServiceMarkers.Should().Contain("report-service").And.Contain("trade-decision-service");
    }

    // 対照（肯定形）その 4: **明示された場所にファイルが無いのは skip ではなく失敗**であること。
    // 「指定したが読めていない」を skip に倒すと、設定ミスが静かな不検査になる。
    [Fact]
    public void 明示された公開許可リストが存在しなければ失敗する()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"ast-mcp-publication-{Guid.NewGuid():N}.json");
        var searched = new List<string>();

        var act = () => LocatePublicationConfig(searched, missing);

        act.Should().Throw<FileNotFoundException>();
    }

    /// <summary>
    /// 本ユニットを指す語。<b>実ツリーの <c>backend/Services</c> から導く</b>（一覧を手で書かない）。
    /// PascalCase のディレクトリ名（<c>ReportService</c>）を、配備で使うケバブケース（<c>report-service</c>）と
    /// 小文字そのまま（<c>reportservice</c>。名前空間 <c>AiStockTrading.ReportService</c> の照合用）の
    /// 両方に展開し、リポジトリ名そのもの（<c>ai-stock-trading</c>）を加える。
    /// </summary>
    private static IReadOnlyList<string> AstServiceMarkers { get; } = BuildMarkers();

    private static IReadOnlyList<string> BuildMarkers()
    {
        var markers = new SortedSet<string>(StringComparer.Ordinal) { "ai-stock-trading", "aistocktrading" };
        foreach (var root in RepositoryLayout.ServiceNamespaceRoots)
        {
            markers.Add(ToKebabCase(root));
            markers.Add(root.ToLowerInvariant());
        }
        return [.. markers];
    }

    private static string ToKebabCase(string pascal)
    {
        var chars = new List<char>(pascal.Length * 2);
        for (var i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i])) chars.Add('-');
            chars.Add(char.ToLowerInvariant(pascal[i]));
        }
        return new string([.. chars]);
    }

    /// <summary>
    /// 違反の抽出。<b>照合は粗い部分一致</b>——IADR-0171 決定2 と同じ理由である。
    /// 公開名がどう綴られるかは基盤側の裁量であり、厳密一致にすると想定と違う綴りが黙って素通りする。
    /// </summary>
    private static IReadOnlyList<string> Violations(IReadOnlyList<PublicationEntry> entries) =>
    [
        .. entries
            .Select(e => (Entry: e, Marker: MatchedMarker($"{e.Service} {e.Name}")))
            .Where(x => x.Marker is not null)
            .Select(x => $"{x.Entry.Service}::{x.Entry.Name}（一致: {x.Marker}）")
            .OrderBy(s => s, StringComparer.Ordinal)
    ];

    private static string? MatchedMarker(string text)
    {
        var normalized = text.ToLowerInvariant();
        return AstServiceMarkers.FirstOrDefault(m => normalized.Contains(m, StringComparison.Ordinal));
    }

    private static IReadOnlyList<PublicationEntry> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("tools", out var tools)
            || tools.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. tools.EnumerateArray().Select(t => new PublicationEntry(
                t.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                t.TryGetProperty("service", out var s) ? s.GetString() ?? string.Empty : string.Empty))
        ];
    }

    /// <summary>
    /// 公開構成の位置を決める。<b>明示（環境変数）＞ 隣接クローン ＞ 見つからない（skip）</b>。
    /// 明示されているのに存在しなければ <see cref="FileNotFoundException"/> を投げる。
    /// </summary>
    private static string? LocatePublicationConfig(List<string> searched, string? overridePath = null)
    {
        overridePath ??= Environment.GetEnvironmentVariable(PathOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            searched.Add(overridePath);
            if (File.Exists(overridePath)) return overridePath;
            throw new FileNotFoundException(
                $"環境変数 {PathOverrideVariable} が指す基盤の MCP 公開許可リストが存在しない: {overridePath}",
                overridePath);
        }

        // 隣接クローンは「リポジトリルートの兄弟」に置かれる想定だが、git worktree では
        // リポジトリルート自体が .claude/worktrees/... の下にある。したがって**祖先を遡って**探す。
        var dir = new DirectoryInfo(RepositoryLayout.Root);
        while (dir is not null)
        {
            var candidate = Path.Combine([dir.FullName, .. AdjacentClonePath]);
            searched.Add(candidate);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return null;
    }
}
