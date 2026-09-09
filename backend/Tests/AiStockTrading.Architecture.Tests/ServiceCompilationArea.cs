namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// NFR, IADR-0312: サービス 1 本ぶんの<b>コンパイル単位</b>（単一プロジェクト＋VSA。IADR-0259）と、
/// そこに属するソース・層フォルダ。
/// <para>
/// <see cref="DomainSourceArea"/> が「Domain 層のソースの置き場」を単位にするのに対し、こちらは
/// <b>プロジェクト（＝コンパイル単位）全体</b>を単位にする。<c>global using</c> は<b>コンパイル単位の
/// 全ファイルへ効く</b>ため、Domain の外に書かれていても Domain のソースの名前解決を変えるからである
/// （#601 の経路 (i)）。層をフォルダで分けた VSA では、この差が<b>コンパイラでは止まらない</b>。
/// </para>
/// </summary>
/// <param name="FullPath">サービスディレクトリの絶対パス（<c>backend/Services/&lt;Svc&gt;/</c>）。</param>
/// <param name="ServiceNamespaceRoot">
/// サービスの<b>ルート名前空間</b>（<c>RiskManagementService</c>）。ディレクトリ名と一致する（IADR-0261）。
/// </param>
internal sealed record ServiceCompilationArea(string FullPath, string ServiceNamespaceRoot)
{
    /// <summary>リポジトリルートからの相対パス（失敗メッセージ用）。</summary>
    public string RelativePath =>
        Path.GetRelativePath(RepositoryLayout.Root, FullPath).Replace('\\', '/');

    /// <summary>
    /// このコンパイル単位に属する <c>.cs</c>。
    /// <list type="bullet">
    ///   <item><c>Tests/</c> は<b>別プロジェクト（別コンパイル単位）</b>であり、そこの
    ///     <c>global using</c> は Domain の名前解決に影響しないため除く。</item>
    ///   <item><c>bin/</c> <c>obj/</c> と <c>*.g.cs</c> は生成物であるため除く
    ///     （<c>ImplicitUsings</c> が吐く <c>GlobalUsings.g.cs</c> を拾うと無意味に赤くなる）。</item>
    /// </list>
    /// </summary>
    public IReadOnlyList<string> SourceFiles { get; } =
        Directory.Exists(FullPath)
            ? Directory.EnumerateFiles(FullPath, "*.cs", SearchOption.AllDirectories)
                .Where(RepositoryLayout.NotUnderBuildOutput)
                .Where(NotUnderTests)
                .Where(p => !p.EndsWith(".g.cs", StringComparison.Ordinal))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray()
            : [];

    /// <summary>
    /// サービス直下の<b>層フォルダ名</b>（<c>Common</c> / <c>Domain</c> / <c>Features</c> /
    /// <c>Hosted</c> / <c>Infrastructure</c> / <c>Tests</c>）。名前空間の第 2 セグメントと一致する
    /// （IADR-0261 の規則）ため、検査 (e) の禁止トークンをここから導ける。
    /// <b>一覧を手で書かない</b> —— 層フォルダが増えても母集合が自動で追随する。
    /// </summary>
    public IReadOnlyList<string> LayerSegments { get; } =
        Directory.Exists(FullPath)
            ? Directory.EnumerateDirectories(FullPath)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(n => n is not ("bin" or "obj"))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray()
            : [];

    private static bool NotUnderTests(string path)
    {
        var normalized = path.Replace('\\', '/');
        return !normalized.Contains("/Tests/", StringComparison.Ordinal);
    }
}
