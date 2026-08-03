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

    private static bool NotUnderBuildOutput(string path)
    {
        var normalized = path.Replace('\\', '/');
        return !normalized.Contains("/bin/", StringComparison.Ordinal)
            && !normalized.Contains("/obj/", StringComparison.Ordinal);
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
