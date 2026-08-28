using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// NFR, platform ADR-0030（§基本方針「Domain 層は外部ライブラリへ依存しない（.NET 標準のみ）」）,
/// IADR-0128 決定 6, IADR-0256:
/// <b>Domain 層の依存規律を、csproj ではなくソースの走査で強制する。</b>
/// <para>
/// 同じ規律を <c>DomainLayerDependencyTests</c> が csproj の静的解析で検査している。
/// <b>本クラスはそれを置き換えるものではなく、二重化するものである。</b>
/// 層をプロジェクトで分ける形（<c>*.Domain.csproj</c>）をやめると csproj 方式は検査対象を失い、
/// <b>「違反なし」で無条件に緑になる</b>——失効が失敗メッセージに現れない種類の壊れ方である。
/// ソース走査は層の置き場がフォルダになっても成立する。
/// </para>
/// <para>
/// 逆に、ソース走査は<b>コンパイラより弱い</b>（<c>global using</c>・ソースジェネレータの生成参照は見えない）。
/// <b>どちらも単独では十分でないので両方を走らせる。</b>
/// </para>
/// </summary>
public class DomainSourceDependencyTests
{
    /// <summary>
    /// 走査対象ファイル数の下限（着手時点の実測 120）。
    /// <b>0 件走査でも「違反 0 件」で緑になる</b>ため、対象が痩せていないことを明示的に固定する。
    /// </summary>
    private const int MinimumDomainSourceFiles = 100;

    /// <summary>
    /// <c>using</c> ディレクティブ数の下限（着手時点の実測 80）。
    /// 解析器が壊れて 1 本も拾わなくなっても、検査 (b) は「違反 0 件」で緑になる。
    /// </summary>
    private const int MinimumScannedUsings = 60;

    /// <summary>
    /// 検査 (c) の禁止トークン数の下限（着手時点の実測 63 ＝ CPM 由来 57 ＋ 実 import の根 14 を重複排除）。
    /// 母集合の導出が壊れて 0 件になれば、走査は何も見つけずに緑になる。
    /// </summary>
    private const int MinimumForbiddenTokens = 30;

    /// <summary>
    /// 🔴 <b>既知の逸脱。</b> Domain から他サービスの名前空間を参照している箇所である
    /// （設計 §1.5・作業仕様書 軸 4 の実測。<b>ファイル 5 件・プロジェクト間の辺 4 本</b>）。
    /// <para>
    /// これらは <c>AiStockTrading.Shared.Kernel</c> の新設（VSA 移行の土台 5）で解消される前提であり、
    /// <b>本一覧は移行が済むまでの暫定である</b>。解消したらこの一覧から削除すること
    /// （削除し忘れは <c>既知の逸脱は今も実際に観測できる</c> が赤で知らせる）。
    /// </para>
    /// <b>一覧に無い他サービス参照が 1 つでも増えたら落ちる。</b>
    /// </summary>
    private static readonly (string RelativePath, string ForeignNamespace)[] KnownForeignReferences =
    [
        ("backend/Services/BacktestService/src/BacktestService.Domain/BacktestCostModel.cs", "AiStockTrading.Configuration"),
        ("backend/Services/BacktestService/src/BacktestService.Domain/Stage0Promotion.cs", "AiStockTrading.RiskManagement"),
        ("backend/Services/CostControlService/src/CostControlService.Domain/CostGovernor.cs", "AiStockTrading.Configuration"),
        ("backend/Services/ReportService/src/ReportService.Domain/LlmUsageRecord.cs", "AiStockTrading.Configuration"),
        ("backend/Services/ReportService/src/ReportService.Domain/PnlAggregator.cs", "AiStockTrading.Configuration"),
    ];

    // ── 検査 (a): 探索そのものが空振りしていないこと ────────────────────────────────
    // 領域が 0 件になると以下の検査はすべて「違反なし」で無条件に緑になる。
    // 検査器が静かに失効する経路を塞ぐメタ検査である（IADR-0127 と同じ性質）。
    [Fact]
    public void Domain_ソース領域の探索が空振りしていない()
    {
        var areas = RepositoryLayout.DomainSourceDirectories;

        areas.Should().HaveCountGreaterThanOrEqualTo(
            9,
            "Domain を持つサービスは実測 9 件（Backtest / Configuration / CostControl / InformationCollection / "
                + "MarketMonitor / OrderExecution / Report / RiskManagement / TradeDecision）である"
                + "（Audit / Notification は Domain を持たない）。"
                + "層がプロジェクトからフォルダへ移っても和集合で数えるため、この下限は移行の前後で成立する。"
                + "実際に見つかったのは: {0}",
            string.Join(", ", areas.Select(a => a.RelativePath)));
    }

    [Fact]
    public void Domain_の走査対象ファイルが痩せていない()
    {
        var files = AllDomainSourceFiles();

        files.Should().HaveCountGreaterThan(
            MinimumDomainSourceFiles,
            "走査対象が痩せると「違反 0 件」が「1 件も読んでいない」と区別できなくなる（着手時点の実測は 120 件）");
    }

    // ── 検査 (b): using は許可リスト内のみ ────────────────────────────────────────
    [Fact]
    public void Domain_の_using_は許可された名前空間だけである()
    {
        var violations = new List<string>();
        foreach (var (area, file) in DomainFilesWithArea())
        {
            foreach (var ns in DomainSourceScan.UsingNamespacesIn(File.ReadAllText(file)))
            {
                if (!DomainSourceScan.IsAllowedDomainNamespace(ns))
                {
                    violations.Add($"{Relative(file)} → using {ns} (service={area.ServiceShortName})");
                }
            }
        }

        violations.Should().BeEmpty(
            "Domain 層が using してよいのは .NET 標準（System.*）・他の Domain・"
                + "AiStockTrading.Shared.Contracts.* ・AiStockTrading.Shared.Kernel.* だけである"
                + "（platform ADR-0030 §基本方針 / IADR-0128 決定 6）。"
                + "外部ライブラリが要る処理は Application / Infrastructure へ置く。違反: {0}",
            string.Join(" / ", violations));
    }

    [Fact]
    public void Domain_の_using_走査が実際にディレクティブを拾っている()
    {
        var count = AllDomainSourceFiles()
            .Sum(f => DomainSourceScan.UsingNamespacesIn(File.ReadAllText(f)).Count);

        count.Should().BeGreaterThan(
            MinimumScannedUsings,
            "using を 1 本も拾えていないと、許可リスト検査は中身を見ずに緑になる（着手時点の実測は 80 本）");
    }

    // ── 検査 (c): 完全修飾での迂回を塞ぐ ────────────────────────────────────────
    [Fact]
    public void Domain_のソースに外部ライブラリの名前空間が現れない()
    {
        var tokens = ForbiddenLibraryTokens();
        var violations = new List<string>();
        foreach (var file in AllDomainSourceFiles())
        {
            var hits = DomainSourceScan.ForbiddenLibraryTokensIn(File.ReadAllText(file), tokens);
            if (hits.Count > 0) violations.Add($"{Relative(file)} → {string.Join(", ", hits)}");
        }

        violations.Should().BeEmpty(
            "using を書かずに完全修飾（Microsoft.EntityFrameworkCore.EF.Property(...) 等）で使えば"
                + "許可リスト検査を迂回できる。禁止トークンは Directory.Packages.props と"
                + "リポジトリ内の実 import から導いており、手で書いた拒否リストではない。違反: {0}",
            string.Join(" / ", violations));
    }

    [Fact]
    public void 外部ライブラリの禁止トークンが中央パッケージ管理から導けている()
    {
        var tokens = ForbiddenLibraryTokens();

        tokens.Should().HaveCountGreaterThan(
            MinimumForbiddenTokens,
            "トークンが導けていないと、走査は何も見つけずに緑になる（着手時点の実測は 63 件）");

        // 導出が「CPM を読めている」ことと「実 import の根も混ざっている」ことを対で押さえる。
        // 片方が壊れても件数の下限だけでは気付けない。
        tokens.Should().Contain("Microsoft.EntityFrameworkCore", "CPM の PackageVersion Include から導く");
        tokens.Should().Contain("Npgsql", "パッケージ ID の全ドット接頭辞を導く");
        tokens.Should().Contain(
            "Wolverine",
            "パッケージ ID（WolverineFx）と名前空間の根（Wolverine）は一致しない。"
                + "リポジトリ内の実 import から導く第 2 の母集合が効いていること");
    }

    // ── 検査 (d): 他サービスの名前空間を参照しない ────────────────────────────────
    [Fact]
    public void Domain_は既知の逸脱を除いて他サービスを参照しない()
    {
        var known = KnownForeignReferences
            .Select(k => $"{k.RelativePath} → {k.ForeignNamespace}")
            .ToHashSet(StringComparer.Ordinal);

        var violations = new List<string>();
        foreach (var (area, file) in DomainFilesWithArea())
        {
            var foreigns = DomainSourceScan.ForeignServiceReferencesIn(
                File.ReadAllText(file), area.ServiceShortName);
            foreach (var foreign in foreigns)
            {
                var entry = $"{Relative(file)} → {foreign}";
                if (!known.Contains(entry)) violations.Add(entry);
            }
        }

        violations.Should().BeEmpty(
            "Domain がサービス境界を跨いで他サービスの Domain を参照すると、"
                + "1 サービス = 1 プロジェクトにした瞬間に相手サービスの永続化・エンドポイント・"
                + "メッセージング配線までビルドへ引き込むことになる。"
                + "共有が要る型は AiStockTrading.Shared.Kernel へ抜くこと。"
                + "既知の逸脱は KnownForeignReferences に列挙してある。一覧に無い違反: {0}",
            string.Join(" / ", violations));
    }

    // 対（肯定形）: 既知の逸脱の一覧が腐っていないこと。
    // 許容一覧は「増やすと検査が弱くなる」ものであり、解消済みの行が残り続けると
    // **本当に増えたときに区別が付かなくなる**。土台 5 で解消したらこのテストが削除を促す。
    [Fact]
    public void 既知の逸脱は今も実際に観測できる()
    {
        var stale = new List<string>();
        foreach (var (relativePath, foreignNamespace) in KnownForeignReferences)
        {
            var full = Path.Combine(RepositoryLayout.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                stale.Add($"{relativePath}（ファイルが存在しない）");
                continue;
            }

            var area = RepositoryLayout.DomainSourceDirectories
                .FirstOrDefault(a => full.StartsWith(a.FullPath + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            if (area is null)
            {
                stale.Add($"{relativePath}（Domain ソース領域の外にある）");
                continue;
            }

            var foreigns = DomainSourceScan.ForeignServiceReferencesIn(File.ReadAllText(full), area.ServiceShortName);
            if (!foreigns.Contains(foreignNamespace)) stale.Add($"{relativePath} → {foreignNamespace}（もう参照していない）");
        }

        stale.Should().BeEmpty(
            "既知の逸脱が解消されたら KnownForeignReferences から削除すること。"
                + "残したままにすると、許容範囲だけが広がって新しい違反を見逃す。解消済み: {0}",
            string.Join(" / ", stale));
    }

    // ── 否定形: 照合器そのものが load-bearing であること ──────────────────────────
    // 実ツリーの違反は現時点で（既知の逸脱を除き）0 件であるため、
    // 照合器が常に「違反なし」を返すよう壊れても上のテストはすべて緑のままである。

    [Theory]
    [InlineData("using System.Text;", "System.Text")]
    [InlineData("global using AiStockTrading.Shared.Contracts.Trading;", "AiStockTrading.Shared.Contracts.Trading")]
    [InlineData("using static System.Math;", "System.Math")]
    [InlineData("using Ef = Microsoft.EntityFrameworkCore;", "Microsoft.EntityFrameworkCore")]
    [InlineData("    using Wolverine;", "Wolverine")]
    public void using_解析器はディレクティブを実際に解析する(string line, string expected)
    {
        DomainSourceScan.TryParseUsingNamespace(line, out var ns).Should().BeTrue();
        ns.Should().Be(expected);
    }

    [Theory]
    [InlineData("using var db = NewContext(dbName);")]
    [InlineData("using (var scope = app.Services.CreateScope())")]
    [InlineData("public sealed record TradeDecision(string Symbol);")]
    [InlineData("// using Microsoft.EntityFrameworkCore は Domain では禁止である")]
    [InlineData("")]
    public void using_解析器は_using_文やコメントを名前空間として解析しない(string line)
    {
        DomainSourceScan.TryParseUsingNamespace(line, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("System")]
    [InlineData("System.Text.Json")]
    [InlineData("AiStockTrading.Shared.Contracts")]
    [InlineData("AiStockTrading.Shared.Contracts.Trading")]
    [InlineData("AiStockTrading.Shared.Kernel.Results")]
    [InlineData("AiStockTrading.RiskManagement.Domain")]
    [InlineData("AiStockTrading.RiskManagement.Domain.Manipulation")]
    public void 許可判定は正当な名前空間を許す(string ns)
    {
        DomainSourceScan.IsAllowedDomainNamespace(ns).Should().BeTrue();
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Npgsql")]
    [InlineData("Wolverine")]
    [InlineData("Xunit")]
    [InlineData("AiStockTrading.Shared.Infrastructure")]
    [InlineData("AiStockTrading.Shared.KnowledgeBase")]
    [InlineData("AiStockTrading.Report.Application")]
    [InlineData("AiStockTrading.Report.Infrastructure.Persistence")]
    [InlineData("Systemic.Things")]
    public void 許可判定は許可外の名前空間を拒む(string ns)
    {
        DomainSourceScan.IsAllowedDomainNamespace(ns).Should().BeFalse();
    }

    [Theory]
    [InlineData("var v = Microsoft.EntityFrameworkCore.EF.Property<int>(e, \"X\");", "Microsoft.EntityFrameworkCore")]
    [InlineData("private readonly Npgsql.NpgsqlConnection _c;", "Npgsql")]
    [InlineData("[Wolverine.Attributes.WolverineHandler]", "Wolverine")]
    public void トークン照合器は完全修飾の参照を実際に検出する(string source, string token)
    {
        DomainSourceScan.ContainsQualifiedNameRoot(source, token).Should().BeTrue();
    }

    [Theory]
    // 直前が `.` のものは修飾名の先頭ではない（メンバアクセス）。誤検出すると Domain の正当な記述が落ちる。
    [InlineData("var o = quote.Open.Value;", "Open")]
    [InlineData("var n = candle.Microsoft.Value;", "Microsoft")]
    // 直前が識別子文字のものも別の名前である。
    [InlineData("var x = MyNpgsql.Thing;", "Npgsql")]
    // 直後が `.` でないものは修飾ではない。
    [InlineData("public sealed record Wolverine(string Name);", "Wolverine")]
    public void トークン照合器は無関係な記述を検出しない(string source, string token)
    {
        DomainSourceScan.ContainsQualifiedNameRoot(source, token).Should().BeFalse();
    }

    [Fact]
    public void 他サービス参照の照合器は他サービスだけを返す()
    {
        const string source = """
            using AiStockTrading.Configuration.Domain;
            using AiStockTrading.Shared.Contracts.Trading;
            namespace AiStockTrading.Backtest.Domain;
            public static class X
            {
                public static object Y() => new AiStockTrading.RiskManagement.Domain.Stage0Promotion();
            }
            """;

        DomainSourceScan.ForeignServiceReferencesIn(source, "Backtest")
            .Should().Equal("AiStockTrading.Configuration", "AiStockTrading.RiskManagement");

        // 自分自身を own として渡せば、同じソースから自サービスは出てこない。
        DomainSourceScan.ForeignServiceReferencesIn(source, "Configuration")
            .Should().NotContain("AiStockTrading.Configuration");
    }

    [Fact]
    public void 他サービス参照の照合器は自サービスと共有物を返さない()
    {
        const string source = """
            using AiStockTrading.Shared.Contracts.Events;
            using AiStockTrading.Shared.Kernel;
            namespace AiStockTrading.Report.Domain;
            """;

        DomainSourceScan.ForeignServiceReferencesIn(source, "Report").Should().BeEmpty();
    }

    [Fact]
    public void 中央パッケージ管理の解析器はパッケージ_ID_を実際に読む()
    {
        const string props = """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.2" />
                <PackageVersion Include="WolverineFx" Version="6.24.5" />
              </ItemGroup>
            </Project>
            """;

        DomainSourceScan.CentralPackageIds(props)
            .Should().Equal("Npgsql.EntityFrameworkCore.PostgreSQL", "WolverineFx");

        DomainSourceScan.TokensFromPackageId("Npgsql.EntityFrameworkCore.PostgreSQL")
            .Should().Equal("Npgsql", "Npgsql.EntityFrameworkCore", "Npgsql.EntityFrameworkCore.PostgreSQL");
    }

    // ── 走査のヘルパ ──────────────────────────────────────────────────────────
    private static IReadOnlyList<string> AllDomainSourceFiles() =>
        RepositoryLayout.DomainSourceDirectories.SelectMany(a => a.SourceFiles).ToArray();

    private static IEnumerable<(DomainSourceArea Area, string File)> DomainFilesWithArea() =>
        RepositoryLayout.DomainSourceDirectories.SelectMany(a => a.SourceFiles.Select(f => (a, f)));

    private static string Relative(string path) =>
        Path.GetRelativePath(RepositoryLayout.Root, path).Replace('\\', '/');

    private static IReadOnlyList<string> ForbiddenLibraryTokens() => LazyTokens.Value;

    private static readonly Lazy<IReadOnlyList<string>> LazyTokens = new(() =>
    {
        var packagesProps = File.ReadAllText(Path.Combine(RepositoryLayout.Root, "Directory.Packages.props"));
        var tokens = new SortedSet<string>(StringComparer.Ordinal);

        // 母集合その 1: 中央パッケージ管理に載る全パッケージ ID とその全ドット接頭辞。
        foreach (var id in DomainSourceScan.CentralPackageIds(packagesProps))
        {
            foreach (var token in DomainSourceScan.TokensFromPackageId(id)) tokens.Add(token);
        }

        // 母集合その 2: リポジトリが実際に import している外部名前空間の根。
        var backendSources = Directory
            .EnumerateFiles(Path.Combine(RepositoryLayout.Root, "backend"), "*.cs", SearchOption.AllDirectories)
            .Where(RepositoryLayout.NotUnderBuildOutput)
            .Select(File.ReadAllText);
        foreach (var root in DomainSourceScan.ExternalNamespaceRootsIn(backendSources)) tokens.Add(root);

        return tokens.ToArray();
    });
}
