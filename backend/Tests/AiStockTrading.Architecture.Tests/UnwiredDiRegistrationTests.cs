using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// NFR, #752, #204 C-3-b, IADR-0335:
/// <b>DI に登録された型に、本番の利用箇所が 1 件もない</b>状態を検出する。
/// <para>
/// 2026-09-02 の go-live 前監査が同型の欠陥を 2 回観測した（D-1 Stage 0 判定 / D-3 維持率割れの自動縮小）。
/// <b>型は在る・テストも通る・しかし本番から呼ばれない</b>ため、既存の CI は 1 つも赤くならない。
/// リポジトリの運用規約「検査器・規約の追加は同型の事故が 2 回起きたら」に達したため本検査を置く。
/// </para>
/// <para>
/// 走査の設計（サービス型だけを判定する理由・走査範囲を推移的参照に閉じる理由・リフレクションを
/// 採らない理由）は <see cref="DiRegistrationScan"/> の解説にある。
/// </para>
/// </summary>
public class UnwiredDiRegistrationTests
{
    /// <summary>
    /// 実ツリーに残る<b>既知の未結線</b>。🔴 <b>無視リストではなくラチェットである</b> ——
    /// 結線されたら「実体を失った項目」で赤くなり、外し忘れが残らない。
    /// <para>
    /// 規約（<c>.claude/rules/traceability.repo.md</c>）が定めるとおり、
    /// <b>暫定の除外は「外す条件」と一緒に書く。条件を書かない除外は恒久化する。</b>
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> KnownUnwired =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OrderExecutionService/OrderAmendmentDispatcher"] =
                "訂正・取消の配管だけが入っており駆動元が呼んでいない（クラス冒頭のコメントが自認）。"
                + "外す条件: 駆動元（時限取消・リコンサイル基点・pause による強制取消）が本型を呼ぶ。"
                + "呼ばれないまま残すなら型ごと消す。",
            ["RiskManagementService/BorrowFeeAccrualService"] =
                "借株料の日次計上。料率の単位が未確定で、取り違えると累計が 100 倍ずれるため"
                + "スケジューラも供給元も登録しない＝意図した遮断（登録位置のコメントが明記）。"
                + "外す条件: 料率の単位が確定し供給元が入って、日次計上の駆動が本型を呼ぶ。",
        };

    private static readonly DiRegistrationAnalysis Repository = DiRegistrationScan.Analyze(DiRegistrationScan.Production());

    // 対（肯定形・先に置く）: 本番プロジェクトの母集合が痩せていないこと。
    // 0 件になると以下の検査は「違反なし」で無条件に緑になる。
    [Fact]
    public void 本番プロジェクトの探索が空振りしていない()
    {
        RepositoryLayout.ProductionProjectFiles.Should().HaveCountGreaterThan(
            14,
            "backend 配下の本番プロジェクト（テスト・TestSupport・横断テストを除く）は "
                + "12 サービス＋BFF＋共有 4 本＝17 本ある。これを下回るなら探索が壊れている。実際に見つかったのは: {0}",
            string.Join(", ", RepositoryLayout.ProductionProjectFiles.Select(Path.GetFileNameWithoutExtension)));

        Repository.Projects.Sum(p => p.Sources.Count).Should().BeGreaterThan(
            800,
            "本番プロジェクトが持つ .cs は実測 962 件である。大きく下回るならファイル走査が壊れている。"
                + "実際に走査したのは {0} 件（プロジェクト {1} 本）",
            Repository.Projects.Sum(p => p.Sources.Count),
            Repository.Projects.Count);
    }

    // 対（肯定形）: DI 登録の走査そのものが空振りしていないこと。
    [Fact]
    public void DI登録の探索が空振りしていない()
    {
        Repository.Registrations.Should().HaveCountGreaterThan(
            150,
            "型引数つきの DI 登録は実測 189 件（AddScoped 87 / AddSingleton 86 / AddHostedService 16）である。"
                + "大きく下回るなら登録の走査が壊れている。実際に見つかったのは {0} 件",
            Repository.Registrations.Count);

        Repository.Registrations.Select(r => r.Kind).Distinct().Should().Contain(
            DiRegistrationScan.HostResolvedKind,
            "常駐サービスの登録も走査対象である（判定からは外すが、走査からは外さない）");
    }

    /// <summary>
    /// 本体。<b>DI に登録した型は、本番のどこかから解決・利用されていなければならない。</b>
    /// </summary>
    [Fact]
    public void DI登録された型には本番の利用箇所がある()
    {
        var violations = Judged()
            .Where(r => !KnownUnwired.ContainsKey(r.Key))
            .Select(r => r.Describe())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();

        violations.Should().BeEmpty(
            "DI に登録した型は、本番のどこかから解決・利用されていなければならない。"
                + "登録だけが在って呼ぶ者が居ない統制は、テストが通っても本番で 1 度も動かない"
                + "（2026-09-02 監査の D-1 / D-3 がこの形）。"
                + "対処は 3 つ: (1) 呼ぶ側を実装する (2) 登録ごと型を消す "
                + "(3) 呼ばれないことが正しいなら UnwiredDiRegistrationTests.KnownUnwired へ"
                + "**理由と外す条件**を添えて載せる。違反: {0}",
            string.Join(" / ", violations));
    }

    /// <summary>
    /// ラチェット。既知リストに<b>実体を失った項目</b>（結線された・型が消えた）が残っていないこと。
    /// これが無いと、直したあとも許容が残り続けて次の同型事故を素通しする。
    /// </summary>
    [Fact]
    public void 既知の未結線リストに実体を失った項目が無い()
    {
        var observed = Judged().Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var stale = KnownUnwired.Keys
            .Where(key => !observed.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        stale.Should().BeEmpty(
            "既知の未結線として許容している項目が、実ツリーではもう未結線ではない"
                + "（結線されたか、型ごと消えたか、名前が変わった）。"
                + "**KnownUnwired から外すこと** —— 残すと次の同型事故を素通しする。実体を失った項目: {0}",
            string.Join(" / ", stale));
    }

    // 否定形（自己試験）: 判定そのものが load-bearing であること。
    // 実ツリーの違反は 0 件であるため、判定が常に「違反なし」を返すよう壊れても上の検査は緑のままになる。
    [Fact]
    public void 判定は呼び出し元の無い登録を検出する()
    {
        var analysis = DiRegistrationScan.Analyze([Synthetic(
            ("Program.cs", "using Svc.Features;\npublic static class Program { static void Main() { builder.Services.AddScoped<Orphan>(); } }"),
            ("Orphan.cs", "namespace Svc.Features;\npublic sealed class Orphan { public void Run() { } }"))]);

        analysis.WithoutProductionUse.Select(r => r.TypeName).Should().Contain(
            "Orphan", "登録しただけで誰も解決しない型は検出されなければならない");
    }

    [Fact]
    public void 判定は呼び出し元のある登録を検出しない()
    {
        var analysis = DiRegistrationScan.Analyze([Synthetic(
            ("Program.cs", "public static class Program { static void Main() { builder.Services.AddScoped<Wired>(); } }"),
            ("Wired.cs", "public sealed class Wired { }"),
            ("Caller.cs", "public sealed class Caller(Wired wired) { }"))]);

        analysis.WithoutProductionUse.Should().BeEmpty(
            "コンストラクタ注入で解決される型は結線済みである。実際に上がったのは: {0}",
            string.Join(" / ", analysis.WithoutProductionUse.Select(r => r.Describe())));
    }

    [Fact]
    public void 判定はエイリアス経由の利用を見落とさない()
    {
        var analysis = DiRegistrationScan.Analyze([Synthetic(
            ("Program.cs", "using AppSvc = Svc.Features.LongNamedAppService;\npublic static class Program { static void Main() { builder.Services.AddScoped<AppSvc>(); } }"),
            ("LongNamedAppService.cs", "namespace Svc.Features;\npublic sealed class LongNamedAppService { }"),
            ("Endpoint.cs", "using AppSvc = Svc.Features.LongNamedAppService;\npublic static class Endpoint { public static void Map() { Get((AppSvc svc) => svc); } }"))]);

        analysis.Registrations.Select(r => r.TypeName).Should().Contain(
            "LongNamedAppService", "登録側の別名は実名へ解決してから数える");
        analysis.WithoutProductionUse.Should().BeEmpty(
            "利用側が別名で書いていても利用は利用である。実際に上がったのは: {0}",
            string.Join(" / ", analysis.WithoutProductionUse.Select(r => r.Describe())));
    }

    [Fact]
    public void 判定はコメントや文字列の中の言及を利用と数えない()
    {
        var analysis = DiRegistrationScan.Analyze([Synthetic(
            ("Program.cs", "public static class Program { static void Main() { builder.Services.AddScoped<Orphan>(); } }"),
            ("Orphan.cs", "public sealed class Orphan { }"),
            ("Mention.cs", "// Orphan がいずれ呼ばれる\npublic sealed class Mention { public string Note => \"Orphan\"; public string Raw => \"\"\"Orphan\"\"\"; }"))]);

        analysis.WithoutProductionUse.Select(r => r.TypeName).Should().Contain(
            "Orphan", "コメント・通常文字列・生文字列の中の言及は「利用」ではない");
    }

    [Fact]
    public void 判定はusing行だけの言及を利用と数えない()
    {
        var analysis = DiRegistrationScan.Analyze([Synthetic(
            ("Program.cs", "using Orphan;\npublic static class Program { static void Main() { builder.Services.AddScoped<Orphan>(); } }"),
            ("Orphan.cs", "public sealed class Orphan { }"))]);

        analysis.WithoutProductionUse.Select(r => r.TypeName).Should().Contain(
            "Orphan", "名前空間の並び（using 行）は型の利用ではない");
    }

    [Fact]
    public void 判定は常駐サービスを対象外にする()
    {
        var analysis = DiRegistrationScan.Analyze([Synthetic(
            ("Program.cs", "public static class Program { static void Main() { builder.Services.AddHostedService<Poller>(); } }"),
            ("Poller.cs", "public sealed class Poller : BackgroundService { }"))]);

        analysis.WithoutProductionUse.Select(r => r.TypeName).Should().Contain(
            "Poller", "走査自体は常駐サービスも拾う（総数の下限検査に効かせるため）");
        Judge(analysis).Should().BeEmpty(
            "常駐サービスはホストが解決して起動するため、呼び出し元ゼロが正しい形である");
    }

    [Fact]
    public void 判定は別サービスの同名型を利用と数えない()
    {
        var analysis = DiRegistrationScan.Analyze(
        [
            new ScannedProject("A", [
                new ScannedSource("A/Program.cs", "public static class Program { static void Main() { builder.Services.AddScoped<IClock>(); } }"),
                new ScannedSource("A/IClock.cs", "public interface IClock { }"),
            ], []),
            new ScannedProject("B", [
                new ScannedSource("B/IClock.cs", "public interface IClock { }"),
                new ScannedSource("B/Uses.cs", "public sealed class Uses(IClock clock) { }"),
            ], []),
        ]);

        analysis.WithoutProductionUse.Select(r => r.Key).Should().Contain(
            "A/IClock",
            "走査範囲を参照グラフに閉じないと、別サービスの同名型が影を作って未結線を見落とす"
                + "（実測: IClock は 9 サービスにそれぞれ別々に宣言されている）");
    }

    /// <summary>実ツリーに対する判定（構造による対象外を落としたもの）。</summary>
    private static IReadOnlyList<DiRegistration> Judged() => Judge(Repository);

    /// <summary>常駐サービス（ホストが解決する契約）を判定から落とす。</summary>
    private static IReadOnlyList<DiRegistration> Judge(DiRegistrationAnalysis analysis) =>
        [.. analysis.WithoutProductionUse.Where(r => r.Kind != DiRegistrationScan.HostResolvedKind)];

    private static ScannedProject Synthetic(params (string Path, string Text)[] sources) =>
        new("Synthetic", [.. sources.Select(s => new ScannedSource(s.Path, s.Text))], []);
}
