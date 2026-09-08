using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// NFR, platform ADR-0030（§基本方針「Domain 層は外部ライブラリへ依存しない（.NET 標準のみ）」）,
/// IADR-0128 決定 6, IADR-0256:
/// <b>Domain 層の依存規律を、csproj ではなくソースの走査で強制する。</b>
/// <para>
/// かつては同じ規律を <c>DomainLayerDependencyTests</c> が csproj の静的解析でも検査しており、
/// 本クラスはそれを<b>二重化する</b>ものだった。移送（IADR-0259）が 11 サービスすべてで完了して
/// <c>*.Domain.csproj</c> が 0 本になった時点で csproj 方式は検査対象を失ったため、
/// <b>IADR-0265 の宣言どおり退役し、規律の強制は本クラスへ一本化された。</b>
/// </para>
/// <para>
/// 🔴 <b>一本化によって、二重化が補っていた弱さがむき出しになった。</b>ソース走査は
/// <b>コンパイラより弱い</b>——層が別プロジェクトだった頃はコンパイラが構造的に防いでいた次の 2 つが、
/// フォルダ境界になった時点で<b>本クラスを素通りしていた</b>（#601。フェーズ末監査が違反を注入して実測）:
/// <list type="number">
///   <item><c>global using</c> 迂回 —— Domain 外のファイルに <c>global using Wolverine;</c> を置くと、
///     Domain のソースは非修飾で外部型を使えてしまう。</item>
///   <item>自サービス他層への<b>完全修飾</b>参照 —— <c>using</c> 形は検査 (b) が止めるが、
///     <c>RiskManagementService.Infrastructure.Persistence.X</c> と完全修飾で書くと止まらない。</item>
/// </list>
/// <b>この 2 つは IADR-0312（#601）で塞いだ</b>——検査 (f) が <c>global using</c> を
/// <b>コンパイル単位（サービス）全体</b>から拾って検査 (b) の許可リストに掛け、検査 (e) が
/// 自サービスの <c>.Domain</c> 以外の層への完全修飾参照を禁止トークンとして検出する。
/// 新設ではなく<b>既知の穴の閉鎖</b>であるため「検査器の追加は同型の事故が 2 回起きたら」の規約には当たらない
/// （規約は<b>新しい規律</b>を足すときの歯止めであり、既存の規律が書き方次第で効かない状態は欠陥である）。
/// <b>他サービスの名前空間と、CPM 由来の外部ライブラリは従前どおり完全修飾でも検出する</b>（検査 (c)・(d)）。
/// <b>実ツリーの違反は (e)・(f) とも 0 件であり、両経路とも注入実験でのみ再現する</b>
/// （閉鎖後の実測: 注入すると (e) は 1 件、(f) は 1 件で赤くなる）。
/// </para>
/// <para>
/// 🔴 <b>ソース走査が弱いこと自体は変わっていない。</b> ソースジェネレータが生成する参照は
/// <c>obj/</c> にしか現れず、本クラスの母集合には入らない（<c>bin/</c> <c>obj/</c> を除外しているため）。
/// NsDepCop のような名前空間依存の<b>ビルド時</b>強制は IADR-0259 で撤回済みであり、
/// 導入するには改定 IADR が要る（IADR-0312 決定 3）。
/// </para>
/// </summary>
public class DomainSourceDependencyTests
{
    /// <summary>
    /// 走査対象ファイル数の下限（IADR-0256 着手時点の実測 120。IADR-0260 の Shared.Kernel 移送後は 117、
    /// IADR-0264 の VersionedAssumptions 移送後は 116）。
    /// <b>0 件走査でも「違反 0 件」で緑になる</b>ため、対象が痩せていないことを明示的に固定する。
    /// </summary>
    private const int MinimumDomainSourceFiles = 100;

    /// <summary>
    /// <c>using</c> ディレクティブ数の下限（IADR-0256 着手時点の実測 80。IADR-0260 の移送後は 87）。
    /// 解析器が壊れて 1 本も拾わなくなっても、検査 (b) は「違反 0 件」で緑になる。
    /// </summary>
    private const int MinimumScannedUsings = 60;

    /// <summary>
    /// 検査 (c) の禁止トークン数の下限（実測 63 ＝ CPM 由来 57 ＋ 実 import の根 14 を重複排除。IADR-0260 の前後で不変）。
    /// 母集合の導出が壊れて 0 件になれば、走査は何も見つけずに緑になる。
    /// </summary>
    private const int MinimumForbiddenTokens = 30;

    /// <summary>
    /// 検査 (f) の走査対象ファイル数の下限（IADR-0312 着手時点の実測 768 ＝ Domain を持つ 10 サービスの
    /// <c>Tests/</c> 以外の <c>.cs</c>。全 11 サービスでは 791）。
    /// <para>
    /// 🔴 <b>下限を「<c>global using</c> の本数」に置くことはできない</b>——実ツリーの <c>global using</c> は
    /// <b>0 本</b>であり（実測。<c>bin/</c> <c>obj/</c> を除く <c>backend</c> 配下）、0 と「解析器が壊れて 0」を
    /// 件数では区別できない。**下限はファイル数に置き**、解析器が load-bearing であることは
    /// 陽性対照のユニットテスト（<c>global_using_解析器は…</c>）で固定する。
    /// </para>
    /// </summary>
    private const int MinimumServiceCompilationSourceFiles = 700;

    /// <summary>
    /// 検査 (e) の禁止トークン数の下限（着手時点の実測 44 ＝ Domain を持つ 10 サービス × 直下の層フォルダ
    /// から <c>Domain</c> を除いた数）。導出が壊れて 0 件になれば、走査は何も見つけずに緑になる。
    /// </summary>
    private const int MinimumCrossLayerTokens = 30;

    /// <summary>
    /// 🔴 <b>既知の逸脱。</b> Domain から他サービスの名前空間を参照している箇所である。
    /// <para>
    /// <b>IADR-0260（VSA 移行の土台 5）で 5 件すべてを解消したため、現在は空である。</b>
    /// 共有が要る型は <c>AiStockTrading.Shared.Kernel</c> へ移した
    /// （<c>TradingAssumptions</c> / <c>CommissionSchedule</c> / <c>MonthlyCostLimits</c> /
    /// <c>TradingAssumptionsDefaults</c> / <c>CostCalculator</c> / <c>TradingStage</c>）。
    /// </para>
    /// <b>空のまま保つこと。</b> ここへ行を足すのは「Domain がサービス境界を跨いだ」ことの追認であり、
    /// 許容範囲が広がるぶんだけ新しい違反を見逃す。足す前に <c>Shared.Kernel</c> へ抜けないかを検討する。
    /// <b>一覧に無い他サービス参照が 1 つでも増えたら落ちる。</b>
    /// </summary>
    private static readonly (string RelativePath, string ForeignNamespace)[] KnownForeignReferences = [];

    // ── 検査 (a): 探索そのものが空振りしていないこと ────────────────────────────────
    // 領域が 0 件になると以下の検査はすべて「違反なし」で無条件に緑になる。
    // 検査器が静かに失効する経路を塞ぐメタ検査である（IADR-0127 と同じ性質）。
    [Fact]
    public void Domain_ソース領域の探索が空振りしていない()
    {
        var areas = RepositoryLayout.DomainSourceDirectories;

        areas.Should().HaveCountGreaterThanOrEqualTo(
            8,
            "Domain を持つサービスは実測 8 件（Backtest / CostControl / InformationCollection / "
                + "MarketMonitor / OrderExecution / Report / RiskManagement / TradeDecision）である"
                + "（Audit / Notification は元から Domain を持たない）。"
                + "🔴 IADR-0264 決定 2 で Configuration が 9 → 8 へ減った —— 設定サービスの Domain に残っていた"
                + "唯一の型 VersionedAssumptions を AiStockTrading.Shared.Kernel へ移した結果、Domain 領域が空になった"
                + "（空の枠は数えない）。**移送でサービスの Domain が空になり得るため、この下限はサービス数ではなく"
                + "「実測 - 0」で読む**。"
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
            "走査対象が痩せると「違反 0 件」が「1 件も読んでいない」と区別できなくなる"
                + "（実測は 116 件。IADR-0256 着手時点は 120 件で、IADR-0260 が共有型 3 ファイルを、"
                + "IADR-0264 が VersionedAssumptions を Shared.Kernel へ移した）");
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
                if (!DomainSourceScan.IsAllowedDomainNamespace(ns, ServiceNamespaceRoots))
                {
                    violations.Add($"{Relative(file)} → using {ns} (service={area.ServiceNamespaceRoot})");
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
            "using を 1 本も拾えていないと、許可リスト検査は中身を見ずに緑になる（実測は 87 本）");
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
                File.ReadAllText(file), area.ServiceNamespaceRoot, ServiceNamespaceRoots);
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

            var foreigns = DomainSourceScan.ForeignServiceReferencesIn(
                File.ReadAllText(full), area.ServiceNamespaceRoot, ServiceNamespaceRoots);
            if (!foreigns.Contains(foreignNamespace)) stale.Add($"{relativePath} → {foreignNamespace}（もう参照していない）");
        }

        stale.Should().BeEmpty(
            "既知の逸脱が解消されたら KnownForeignReferences から削除すること。"
                + "残したままにすると、許容範囲だけが広がって新しい違反を見逃す。解消済み: {0}",
            string.Join(" / ", stale));
    }

    // ── 検査 (e): 自サービスの他層への完全修飾参照を塞ぐ（#601 経路 (ii)。IADR-0312 決定 2）──
    [Fact]
    public void Domain_は自サービスの他層を完全修飾でも参照しない()
    {
        var violations = new List<string>();
        foreach (var (area, file) in DomainFilesWithArea())
        {
            var segments = LayerSegmentsOf(area.ServiceNamespaceRoot);
            var hits = DomainSourceScan.OwnServiceCrossLayerReferencesIn(
                File.ReadAllText(file), area.ServiceNamespaceRoot, segments);
            if (hits.Count > 0) violations.Add($"{Relative(file)} → {string.Join(", ", hits)}");
        }

        violations.Should().BeEmpty(
            "Domain は自サービスの中でも Domain 以外の層（Infrastructure / Features / Hosted / Common / Tests）へ"
                + "依存してはならない。層が別プロジェクトだった頃はコンパイラが構造的に防いでいたが、"
                + "VSA でフォルダ境界になった今は検査器が止めるほかない（IADR-0259 / IADR-0312）。"
                + "🔴 using 形は検査 (b) が止めるので、**書き方によって結果が変わらない**ようにするのが本検査である。"
                + "違反: {0}",
            string.Join(" / ", violations));
    }

    [Fact]
    public void 自サービス他層の禁止トークンが実ツリーの層フォルダから導けている()
    {
        var tokens = RepositoryLayout.DomainBearingCompilationAreas
            .SelectMany(a => DomainSourceScan.CrossLayerTokensFor(a.ServiceNamespaceRoot, a.LayerSegments))
            .ToArray();

        tokens.Should().HaveCountGreaterThan(
            MinimumCrossLayerTokens,
            "トークンが導けていないと、検査 (e) は何も見つけずに緑になる（着手時点の実測は 44 件）。"
                + "実際に導けたのは: {0}",
            string.Join(", ", tokens));

        // 導出が「サービスのルート」と「実在する層フォルダ」の積になっていることを対で押さえる。
        tokens.Should().Contain("RiskManagementService.Infrastructure");
        tokens.Should().Contain("RiskManagementService.Features");

        // Domain だけは唯一許される層である。禁止トークンへ混ざると Domain の全ファイルが違反になる。
        tokens.Should().NotContain(t => t.EndsWith($".{DomainSourceScan.DomainSegment}", StringComparison.Ordinal));
    }

    // ── 検査 (f): global using は Domain 外でも許可リスト内のみ（#601 経路 (i)。IADR-0312 決定 1）──
    [Fact]
    public void サービス全体の_global_using_は_Domain_の許可リストを破らない()
    {
        var violations = new List<string>();
        foreach (var area in RepositoryLayout.DomainBearingCompilationAreas)
        {
            foreach (var file in area.SourceFiles)
            {
                foreach (var ns in DomainSourceScan.GlobalUsingNamespacesIn(File.ReadAllText(file)))
                {
                    if (!DomainSourceScan.IsAllowedDomainNamespace(ns, ServiceNamespaceRoots))
                    {
                        violations.Add($"{Relative(file)} → global using {ns} (service={area.ServiceNamespaceRoot})");
                    }
                }
            }
        }

        violations.Should().BeEmpty(
            "global using は**コンパイル単位の全ファイルへ効く**。単一プロジェクト＋VSA では Domain も"
                + "同じコンパイル単位に居るため、Domain 外の global using Wolverine; ひとつで"
                + "Domain のソースが非修飾で外部型を使えるようになる（検査 (b) は Domain/ 配下しか見ず、"
                + "検査 (c) は本文に文字列が現れないと当たらない）。"
                + "外部ライブラリを global using したい場合でも、Domain を持つサービスでは置けない。違反: {0}",
            string.Join(" / ", violations));
    }

    [Fact]
    public void サービス全体の走査対象が痩せていない()
    {
        var areas = RepositoryLayout.DomainBearingCompilationAreas;
        var files = areas.SelectMany(a => a.SourceFiles).ToArray();

        areas.Should().HaveCountGreaterThanOrEqualTo(
            8,
            "Domain を持つサービスは実測 10 件である（Configuration は IADR-0264 で Domain が空になった）。"
                + "検査 (a) と同じ下限を置き、母集合が片方だけ痩せる形を防ぐ。実際に見つかったのは: {0}",
            string.Join(", ", areas.Select(a => a.RelativePath)));

        files.Should().HaveCountGreaterThan(
            MinimumServiceCompilationSourceFiles,
            "走査対象が痩せると「global using の違反 0 件」が「1 件も読んでいない」と区別できなくなる"
                + "（着手時点の実測は 768 件。Tests/ と bin/ obj/ と *.g.cs を除く）");
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
    [InlineData("RiskManagementService.Domain")]
    [InlineData("RiskManagementService.Domain.Manipulation")]
    public void 許可判定は正当な名前空間を許す(string ns)
    {
        DomainSourceScan.IsAllowedDomainNamespace(ns, ServiceNamespaceRoots).Should().BeTrue();
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Npgsql")]
    [InlineData("Wolverine")]
    [InlineData("Xunit")]
    [InlineData("AiStockTrading.Shared.Infrastructure")]
    [InlineData("AiStockTrading.Shared.KnowledgeBase")]
    [InlineData("ReportService.Application")]
    [InlineData("ReportService.Infrastructure.Persistence")]
    [InlineData("Systemic.Things")]
    // 実ツリーに無いサービス名は根として認めない（fail-closed。IADR-0261）。
    [InlineData("PhantomService.Domain")]
    public void 許可判定は許可外の名前空間を拒む(string ns)
    {
        DomainSourceScan.IsAllowedDomainNamespace(ns, ServiceNamespaceRoots).Should().BeFalse();
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
            using ConfigurationService.Domain;
            using AiStockTrading.Shared.Contracts.Trading;
            namespace BacktestService.Domain;
            public static class X
            {
                // クラス名の言及（StageGateService.EffectivePolicy()）は他サービスの根ではない。
                public static object Y() => new RiskManagementService.Domain.Stage0Promotion();
            }
            """;

        DomainSourceScan.ForeignServiceReferencesIn(source, "BacktestService", ServiceNamespaceRoots)
            .Should().Equal("ConfigurationService", "RiskManagementService");

        // 自分自身を own として渡せば、同じソースから自サービスは出てこない。
        DomainSourceScan.ForeignServiceReferencesIn(source, "ConfigurationService", ServiceNamespaceRoots)
            .Should().NotContain("ConfigurationService");
    }

    [Fact]
    public void 他サービス参照の照合器は自サービスと共有物を返さない()
    {
        const string source = """
            using AiStockTrading.Shared.Contracts.Events;
            using AiStockTrading.Shared.Kernel;
            namespace ReportService.Domain;
            """;

        DomainSourceScan.ForeignServiceReferencesIn(source, "ReportService", ServiceNamespaceRoots)
            .Should().BeEmpty();
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

    // ── 否定形（#601 経路 (i)）: global using の解析器と許可リストの結線 ──────────────
    [Theory]
    [InlineData("global using Wolverine;", "Wolverine")]
    [InlineData("  global using static System.Math;", "System.Math")]
    [InlineData("global using Ef = Microsoft.EntityFrameworkCore;", "Microsoft.EntityFrameworkCore")]
    public void global_using_解析器はディレクティブを実際に解析する(string line, string expected)
    {
        DomainSourceScan.TryParseGlobalUsingNamespace(line, out var ns).Should().BeTrue();
        ns.Should().Be(expected);
    }

    [Theory]
    // 通常の using はそのファイルにしか効かない。検査 (f) の対象ではない（Domain 内なら検査 (b) が見る）。
    [InlineData("using Wolverine;")]
    [InlineData("// global using Wolverine;")]
    [InlineData("using var db = NewContext(dbName);")]
    [InlineData("")]
    public void global_using_解析器は通常の_using_やコメントを拾わない(string line)
    {
        DomainSourceScan.TryParseGlobalUsingNamespace(line, out _).Should().BeFalse();
    }

    [Fact]
    public void 経路_i_の注入_Domain外の_global_using_は許可リストが拒む()
    {
        // Features 層のファイル（＝Domain の外）に置かれた global using。同じコンパイル単位に居る
        // Domain のソースが **非修飾で** Wolverine の型を使えるようになる（#601 経路 (i)）。
        const string featuresFile = """
            global using Wolverine;
            global using AiStockTrading.Shared.Kernel;
            namespace RiskManagementService.Features.RiskManagement.ClosePosition;
            """;

        var globals = DomainSourceScan.GlobalUsingNamespacesIn(featuresFile);
        globals.Should().Equal("Wolverine", "AiStockTrading.Shared.Kernel");

        DomainSourceScan.IsAllowedDomainNamespace(globals[0], ServiceNamespaceRoots).Should().BeFalse();
        DomainSourceScan.IsAllowedDomainNamespace(globals[1], ServiceNamespaceRoots).Should().BeTrue();
    }

    // ── 否定形（#601 経路 (ii)）: 自サービス他層の照合器 ──────────────────────────
    [Fact]
    public void 自サービス他層のトークンは層フォルダから導かれ_Domain_は含まない()
    {
        DomainSourceScan.CrossLayerTokensFor(
                "RiskManagementService", ["Common", "Domain", "Features", "Hosted", "Infrastructure", "Tests"])
            .Should().Equal(
                "RiskManagementService.Common",
                "RiskManagementService.Features",
                "RiskManagementService.Hosted",
                "RiskManagementService.Infrastructure",
                "RiskManagementService.Tests");
    }

    [Fact]
    public void 経路_ii_の注入_自サービス他層への完全修飾参照を検出する()
    {
        const string domainFile = """
            namespace RiskManagementService.Domain;
            public static class Probe
            {
                // using を書かずに完全修飾で書くと、検査 (b) にも検査 (d) にも当たらない（#601 経路 (ii)）。
                public static object Context() =>
                    new RiskManagementService.Infrastructure.Persistence.RiskManagementDbContext();
            }
            """;

        DomainSourceScan.OwnServiceCrossLayerReferencesIn(domainFile, "RiskManagementService", SampleLayerSegments)
            .Should().Equal("RiskManagementService.Infrastructure");
    }

    [Fact]
    public void 自サービス他層の照合器は正当な書き方を検出しない()
    {
        const string domainFile = """
            using AiStockTrading.Shared.Kernel;
            namespace RiskManagementService.Domain;

            // 永続化は RiskManagementService.Infrastructure.Persistence 側で解決する（コメントの言及は違反ではない）。
            public static class Policy
            {
                /* RiskManagementService.Features.RiskManagement.ClosePosition から呼ばれる */
                public const string Marker = "RiskManagementService.Hosted.Worker";

                public static RiskManagementService.Domain.Manipulation.Detector? Detector() => null;
            }
            """;

        DomainSourceScan.OwnServiceCrossLayerReferencesIn(domainFile, "RiskManagementService", SampleLayerSegments)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("var x = 1; // RiskManagementService.Infrastructure.Db を使ってはいけない")]
    [InlineData("/* RiskManagementService.Infrastructure.Db */ var x = 1;")]
    [InlineData("var s = \"RiskManagementService.Infrastructure.Db\";")]
    [InlineData("var s = @\"RiskManagementService.Infrastructure.Db\";")]
    [InlineData("var s = $\"{RiskManagementService.Infrastructure.Db}\";")]
    public void コメントと文字列リテラルは本文から取り除かれる(string source)
    {
        DomainSourceScan.StripCommentsAndStringLiterals(source)
            .Should().NotContain("RiskManagementService.Infrastructure");
    }

    [Fact]
    public void 生文字列リテラルも取り除かれる()
    {
        const string source = """"
            var sql = """
                RiskManagementService.Infrastructure.Persistence
                """;
            """";

        DomainSourceScan.StripCommentsAndStringLiterals(source)
            .Should().NotContain("RiskManagementService.Infrastructure");
    }

    [Theory]
    // 対（肯定形）: 除去器がコードまで消してしまうと、検査 (e) は何も見つけずに緑になる。
    [InlineData("public static RiskManagementService.Domain.Stage0Promotion P() => new();", "RiskManagementService.Domain")]
    [InlineData("// コメント\nRiskManagementService.Infrastructure.Db _db;", "RiskManagementService.Infrastructure")]
    [InlineData("var s = \"文字列\"; RiskManagementService.Features.X.Y();", "RiskManagementService.Features")]
    public void コードとして書かれた修飾名は残る(string source, string expected)
    {
        DomainSourceScan.StripCommentsAndStringLiterals(source).Should().Contain(expected);
    }

    /// <summary>照合器のユニットテスト用の層セグメント（実ツリーの実測と同じ形）。</summary>
    private static readonly string[] SampleLayerSegments =
        ["Common", "Domain", "Features", "Hosted", "Infrastructure", "Tests"];

    // ── 走査のヘルパ ──────────────────────────────────────────────────────────
    private static IReadOnlyList<string> AllDomainSourceFiles() =>
        RepositoryLayout.DomainSourceDirectories.SelectMany(a => a.SourceFiles).ToArray();

    private static IEnumerable<(DomainSourceArea Area, string File)> DomainFilesWithArea() =>
        RepositoryLayout.DomainSourceDirectories.SelectMany(a => a.SourceFiles.Select(f => (a, f)));

    /// <summary>サービスのルート名前空間（実ツリー由来。IADR-0261）。</summary>
    private static IReadOnlyList<string> ServiceNamespaceRoots => RepositoryLayout.ServiceNamespaceRoots;

    /// <summary>
    /// サービス直下の層フォルダ名（実ツリー由来。IADR-0312）。
    /// <b>見つからなければ例外で落とす</b>——空を返すと検査 (e) が黙って 0 件検査になる。
    /// </summary>
    private static IReadOnlyList<string> LayerSegmentsOf(string serviceNamespaceRoot)
    {
        var area = RepositoryLayout.ServiceCompilationAreas.FirstOrDefault(
            a => string.Equals(a.ServiceNamespaceRoot, serviceNamespaceRoot, StringComparison.Ordinal));

        return area?.LayerSegments
            ?? throw new InvalidOperationException(
                $"サービス {serviceNamespaceRoot} のコンパイル単位が backend/Services 配下に見つからない。");
    }

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
        foreach (var root in DomainSourceScan.ExternalNamespaceRootsIn(
                     backendSources, RepositoryLayout.ServiceNamespaceRoots))
        {
            tokens.Add(root);
        }

        return tokens.ToArray();
    });
}
