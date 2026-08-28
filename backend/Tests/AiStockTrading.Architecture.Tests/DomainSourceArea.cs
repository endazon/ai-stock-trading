namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// NFR, IADR-0256: Domain 層の<b>ソースが置かれている領域</b>（ディレクトリ）と、その領域が属するサービス。
/// <para>
/// 層をプロジェクトで分ける現行構成では <c>backend/Services/&lt;Svc&gt;Service/src/&lt;Svc&gt;Service.Domain/</c>、
/// Vertical Slice 構成では <c>backend/Services/&lt;Svc&gt;Service/Domain/</c> が該当する。
/// <b>どちらの形でも「Domain 層のソースの置き場」であることは変わらない</b>ため、
/// 依存規律の検査はこの領域を単位にする（csproj を単位にすると、層をプロジェクトで分けなくなった瞬間に
/// 検査対象が 0 件になり、静かに緑へ落ちる）。
/// </para>
/// </summary>
/// <param name="FullPath">Domain ソースを含むディレクトリの絶対パス。</param>
/// <param name="ServiceShortName">
/// 属するサービスの短縮名（<c>BacktestService</c> → <c>Backtest</c>）。
/// <b>実測で、名前空間 <c>AiStockTrading.&lt;Short&gt;.Domain</c> の <c>&lt;Short&gt;</c> と一致する</b>
/// （10 種類の名前空間宣言すべてで確認。作業仕様書 軸 3）。この一致が「自サービスか他サービスか」の
/// 機械判定の根拠である。
/// </param>
internal sealed record DomainSourceArea(string FullPath, string ServiceShortName)
{
    /// <summary>リポジトリルートからの相対パス（失敗メッセージ用）。</summary>
    public string RelativePath =>
        Path.GetRelativePath(RepositoryLayout.Root, FullPath).Replace('\\', '/');

    /// <summary>この領域に属する <c>.cs</c>（ビルド成果物を除く）。</summary>
    public IReadOnlyList<string> SourceFiles { get; } =
        Directory.Exists(FullPath)
            ? Directory.EnumerateFiles(FullPath, "*.cs", SearchOption.AllDirectories)
                .Where(RepositoryLayout.NotUnderBuildOutput)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray()
            : [];
}
