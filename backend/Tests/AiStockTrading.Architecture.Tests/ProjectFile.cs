using System.Xml.Linq;

namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// NFR, IADR-0128, platform ADR-0030: `.csproj` を「依存の宣言」として読むための最小の静的解析。
/// ビルド成果物・推移解決に依存しないため、失敗メッセージが「どの csproj のどの参照が違反か」を直接指せる。
/// </summary>
internal sealed class ProjectFile
{
    private ProjectFile(string path, IReadOnlyList<string> packageReferences, IReadOnlyList<string> projectReferences)
    {
        Path = path;
        PackageReferences = packageReferences;
        ProjectReferences = projectReferences;
    }

    /// <summary>csproj の絶対パス。</summary>
    public string Path { get; }

    /// <summary>リポジトリルートからの相対パス（失敗メッセージ用）。</summary>
    public string RelativePath => System.IO.Path.GetRelativePath(RepositoryLayout.Root, Path).Replace('\\', '/');

    /// <summary>ファイル名から拡張子を除いたもの（＝アセンブリ名。本リポジトリでは一致する）。</summary>
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary><c>PackageReference</c> の Include 値。</summary>
    public IReadOnlyList<string> PackageReferences { get; }

    /// <summary><c>ProjectReference</c> が指す csproj の絶対パス。</summary>
    public IReadOnlyList<string> ProjectReferences { get; }

    public static ProjectFile Load(string csprojPath)
    {
        var full = System.IO.Path.GetFullPath(csprojPath);
        var dir = System.IO.Path.GetDirectoryName(full)!;
        var doc = XDocument.Load(full);

        // SDK 形式の csproj は既定名前空間を持たないため、要素名のローカル名で拾う。
        var packages = doc.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update"))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToArray();

        var projects = doc.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, v!.Trim().Replace('\\', System.IO.Path.DirectorySeparatorChar))))
            .ToArray();

        return new ProjectFile(full, packages, projects);
    }
}
