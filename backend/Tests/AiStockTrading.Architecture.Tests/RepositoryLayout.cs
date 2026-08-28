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
    /// サービスディレクトリ名からサービス短縮名を導く（<c>BacktestService</c> → <c>Backtest</c>）。
    /// 実測で名前空間 <c>AiStockTrading.&lt;Short&gt;.Domain</c> の <c>&lt;Short&gt;</c> と一致する。
    /// </summary>
    public static string ServiceShortName(string serviceDirectoryName) =>
        serviceDirectoryName.EndsWith("Service", StringComparison.Ordinal)
            ? serviceDirectoryName[..^"Service".Length]
            : serviceDirectoryName;

    private static IReadOnlyList<DomainSourceArea> BuildDomainSourceDirectories()
    {
        var servicesRoot = Path.Combine(Root, "backend", "Services");
        var areas = new SortedDictionary<string, DomainSourceArea>(StringComparer.Ordinal);

        foreach (var serviceDir in Directory.EnumerateDirectories(servicesRoot))
        {
            var shortName = ServiceShortName(Path.GetFileName(serviceDir));

            // 形 1（現行）: src/<Svc>Service.Domain/ ——「Domain 層のプロジェクト」のディレクトリ。
            var srcDir = Path.Combine(serviceDir, "src");
            if (Directory.Exists(srcDir))
            {
                foreach (var projectDir in Directory.EnumerateDirectories(srcDir))
                {
                    if (!Path.GetFileName(projectDir).EndsWith(".Domain", StringComparison.Ordinal)) continue;
                    Add(projectDir, shortName);
                }
            }

            // 形 2（Vertical Slice 移行後）: <Svc>Service/Domain/ ——「Domain 層のフォルダ」。
            Add(Path.Combine(serviceDir, "Domain"), shortName);
        }

        return areas.Values.ToArray();

        void Add(string directory, string shortName)
        {
            if (!Directory.Exists(directory)) return;
            var area = new DomainSourceArea(Path.GetFullPath(directory), shortName);

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
