namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// NFR, IADR-0128: リポジトリ上のプロジェクト配置を「実ツリーの走査」で得る。
/// 一覧をハードコードするとプロジェクトの追加漏れ（＝検査されないプロジェクト）が静かに発生するため採らない。
/// </summary>
internal static class RepositoryLayout
{
    /// <summary>リポジトリルート（`backend/backend.slnx` を持つ最も近い祖先ディレクトリ）。</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>`backend/Services` 配下の全 `*.csproj`。</summary>
    public static IReadOnlyList<string> ServiceProjectFiles { get; } =
        Directory.EnumerateFiles(Path.Combine(Root, "backend", "Services"), "*.csproj", SearchOption.AllDirectories)
            .Where(NotUnderBuildOutput)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Domain 層のプロジェクト。プロジェクト名の接尾辞 <c>.Domain</c> で識別する
    /// （命名規則は IADR-0128 決定 3。`<Svc>.<Layer>` の Layer セグメントが層を表す唯一の情報源である）。
    /// </summary>
    public static IReadOnlyList<string> DomainProjectFiles { get; } =
        ServiceProjectFiles
            .Where(p => Path.GetFileNameWithoutExtension(p).EndsWith(".Domain", StringComparison.Ordinal))
            .ToArray();

    /// <summary>
    /// NFR, IADR-0265: <c>*.Domain.csproj</c> を持つ<b>未移送</b>サービス（旧構成の <c>src/</c> が実在し、
    /// その配下に <c>*.Domain</c> ディレクトリを持つサービス）の数。<c>DomainProjectFiles</c> と同じ実測に
    /// 収束するはずの値だが、**探索の経路をあえて分ける**（本プロパティはサービスディレクトリの
    /// 直下 <c>src/</c> を見るだけで <c>*.csproj</c> の中身までは読まない）。
    /// <para>
    /// 単一プロジェクト＋VSA への移送（IADR-0259）が 1 サービス進むたびに、ここでの母集合からも
    /// <c>DomainProjectFiles</c> の実測からも同じ 1 件が消える。<c>DomainLayerDependencyTests</c> の
    /// メタ検査（探索が空振りしていないこと）の下限を、移送のたびに手で書き換える数値ではなく
    /// ここから動的に導く——2 件減らすべきところを 1 件しか減らさない、逆に減らし過ぎる、といった
    /// 手書き更新の事故を避けるためである。
    /// </para>
    /// </summary>
    public static int UnmigratedServicesWithDomainProjectCount { get; } =
        Directory.EnumerateDirectories(Path.Combine(Root, "backend", "Services"))
            .Count(serviceDir =>
            {
                var srcDir = Path.Combine(serviceDir, "src");
                var srcExists = Directory.Exists(srcDir);
                var subdirNames = srcExists
                    ? Directory.EnumerateDirectories(srcDir).Select(Path.GetFileName)!
                    : Enumerable.Empty<string>();
                return CountsAsUnmigratedServiceWithDomainProject(srcExists, subdirNames!);
            });

    /// <summary>
    /// NFR, IADR-0265: 「未移送で Domain を持つサービス」の判定を、実ディスク I/O から切り離した
    /// 純関数として切り出したもの（<c>UnmigratedServicesWithDomainProjectCount</c> の実測ロジック本体）。
    /// ファイルシステムに触れずに肯定・否定の両方を固定できるようにするための切り出しであり、
    /// 自己試験は <c>DomainLayerDependencyTests</c> 側に置く（消費側と同じ場所で読めるように）。
    /// </summary>
    internal static bool CountsAsUnmigratedServiceWithDomainProject(
        bool srcDirectoryExists, IEnumerable<string> srcSubdirectoryNames) =>
        srcDirectoryExists
        && srcSubdirectoryNames.Any(name => name.EndsWith(".Domain", StringComparison.Ordinal));

    /// <summary>ビルド成果物（<c>bin/</c> <c>obj/</c>）の下でないこと。</summary>
    public static bool NotUnderBuildOutput(string path)
    {
        var normalized = path.Replace('\\', '/');
        return !normalized.Contains("/bin/", StringComparison.Ordinal)
            && !normalized.Contains("/obj/", StringComparison.Ordinal);
    }

    /// <summary>
    /// NFR, IADR-0256: <c>backend/Shared</c> 配下の全 <c>*.csproj</c>。
    /// 共有プロジェクトは Vertical Slice 移行後も csproj のまま残るため、
    /// <b>csproj 静的解析による依存規律の検査はここに残り続ける</b>（設計 §2.3 (e)）。
    /// </summary>
    public static IReadOnlyList<string> SharedProjectFiles { get; } =
        Directory.EnumerateFiles(Path.Combine(Root, "backend", "Shared"), "*.csproj", SearchOption.AllDirectories)
            .Where(NotUnderBuildOutput)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// NFR, IADR-0256: Domain 層の<b>ソース領域</b>。現行構成（層＝プロジェクト）と
    /// Vertical Slice 構成（層＝フォルダ）の<b>両方の形を数える和集合</b>である。
    /// <para>
    /// 移行は 1 サービスずつ進むため、期間中は新旧が必ず混在する。片方だけを数えると
    /// <b>移行が進むほど検査対象が痩せていく</b>——しかもそれは失敗メッセージに現れない。
    /// </para>
    /// </summary>
    public static IReadOnlyList<DomainSourceArea> DomainSourceDirectories { get; } = BuildDomainSourceDirectories();

    /// <summary>
    /// NFR, IADR-0261: サービスの<b>ルート名前空間</b>の集合。<c>backend/Services</c> のディレクトリ名
    /// （<c>BacktestService</c>）がそのままルート名前空間（<c>BacktestService.Domain</c> の第 1 セグメント）である
    /// （基盤 MSP:IADR-0282 決定 3「ルート名前空間は <c>&lt;Name&gt;</c>」と同じ規則）。
    /// <para>
    /// <b>一覧を手で書かない。</b> 実ツリーから引くため、サービスが増減しても検査の母集合が自動で追随する。
    /// 「末尾が <c>Service</c> の識別子」という形の判定は採らない —— <c>StageGateService</c> /
    /// <c>RiskSettingsService</c> のような<b>クラス名</b>を名前空間の根と誤認する（実測で Domain のコメントに 3 件ある）。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> ServiceNamespaceRoots { get; } =
        Directory.EnumerateDirectories(Path.Combine(Root, "backend", "Services"))
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<DomainSourceArea> BuildDomainSourceDirectories()
    {
        var servicesRoot = Path.Combine(Root, "backend", "Services");
        var areas = new SortedDictionary<string, DomainSourceArea>(StringComparer.Ordinal);

        foreach (var serviceDir in Directory.EnumerateDirectories(servicesRoot))
        {
            var namespaceRoot = Path.GetFileName(serviceDir);

            // 形 1（現行）: src/<Svc>Service.Domain/ ——「Domain 層のプロジェクト」のディレクトリ。
            var srcDir = Path.Combine(serviceDir, "src");
            if (Directory.Exists(srcDir))
            {
                foreach (var projectDir in Directory.EnumerateDirectories(srcDir))
                {
                    if (!Path.GetFileName(projectDir).EndsWith(".Domain", StringComparison.Ordinal)) continue;
                    Add(projectDir, namespaceRoot);
                }
            }

            // 形 2（Vertical Slice 移行後）: <Svc>Service/Domain/ ——「Domain 層のフォルダ」。
            Add(Path.Combine(serviceDir, "Domain"), namespaceRoot);
        }

        return areas.Values.ToArray();

        void Add(string directory, string namespaceRoot)
        {
            if (!Directory.Exists(directory)) return;
            var area = new DomainSourceArea(Path.GetFullPath(directory), namespaceRoot);

            // .cs を 1 つも含まない枠は数えない（空の枠で下限検査を水増しさせない）。
            if (area.SourceFiles.Count == 0) return;
            areas[area.FullPath] = area;
        }
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "backend", "backend.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"リポジトリルート（backend/backend.slnx を持つディレクトリ）を {AppContext.BaseDirectory} から遡って見つけられなかった。");
    }
}
