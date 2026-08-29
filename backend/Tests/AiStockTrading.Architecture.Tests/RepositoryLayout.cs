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

    // NFR, IADR-0265 フォローアップ, IADR-0259: かつてここには DomainProjectFiles（`*.Domain.csproj` の走査）と
    // UnmigratedServicesWithDomainProjectCount（未移送サービスの実測）があり、DomainLayerDependencyTests が
    // csproj の静的解析で Domain 層の依存規律を強制していた。単一プロジェクト＋VSA への移送が 11 サービス
    // すべてで完了し **`*.Domain.csproj` が 0 本になった**ため、その検査は入力集合が空になり構造的に
    // 何も検査できなくなった——IADR-0265 が仕込んだ「0 件になったら役目を終えたと名指しして落ちる」
    // メタ検査が設計どおり作動したので、宣言どおりクラスごと退役させた。
    //
    // 🔴 規律そのものは失っていない。後継は DomainSourceDependencyTests（IADR-0256 で二重化のために新設）で、
    // Domain の**ソース**を新旧樹形の和集合で走査し、using 許可リスト・CPM 由来の禁止トークン（完全修飾での
    // 迂回を塞ぐ）・他サービス参照を検査する。走査件数の下限検査を持つため「0 件走査で緑」にはならない。

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
