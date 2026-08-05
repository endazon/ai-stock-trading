using System.Runtime.CompilerServices;

namespace AiStockTrading.RiskManagement.Api.Tests.Contracts;

// FR-10, FR-20, SC-02, SC-03, #389, IADR-0146: 契約フィクスチャ（フロントが読む実応答 JSON）の置き場と入出力。
//
// フィクスチャは **frontend 側に置く**（`frontend/src/features/risk/contract-fixtures/`）。フロントの
// `tsc` / `vitest` が `import` して型と突き合わせるためであり、単独リポとしての frontend の自己完結性
// （IADR-0080）を壊さないためでもある（platform へ合成されるときにフィクスチャも一緒に運ばれる）。
internal static class ContractFixtureStore
{
    private const string FixtureRelativePath = "frontend/src/features/risk/contract-fixtures";

    /// <summary>
    /// 明示オプトイン時のみフィクスチャを再生成する（IADR-0146 決定5）。
    /// 自動更新にすると比較が常に緑になり、機構が丸ごと無効化される。
    /// </summary>
    public static bool UpdateRequested =>
        Environment.GetEnvironmentVariable("UPDATE_CONTRACT_FIXTURES") is "1" or "true";

    public static string PathOf(string fileName) => Path.Combine(Directory, fileName);

    public static string Read(string fileName)
    {
        var path = PathOf(fileName);
        if (!File.Exists(path))
        {
            // 欠落は skip ではなく失敗にする。フィクスチャが無い＝検査が空振りしている状態であり、
            // 黙って通すと「緑だが何も見ていない」CI に退行する。
            throw new FileNotFoundException(
                $"契約フィクスチャ `{path}` がありません。`UPDATE_CONTRACT_FIXTURES=1 dotnet test` で生成し、"
                + "フロント（contractFixtures.ts）を追随させてからコミットしてください。", path);
        }

        return File.ReadAllText(path);
    }

    public static void Write(string fileName, string normalizedJson)
    {
        System.IO.Directory.CreateDirectory(Directory);
        // 末尾改行を付ける（テキストファイルの慣行。差分が末尾行で汚れない）。
        File.WriteAllText(PathOf(fileName), normalizedJson.ReplaceLineEndings("\n") + "\n");
    }

    /// <summary>フィクスチャ格納ディレクトリの絶対パス。</summary>
    public static string Directory { get; } = Path.Combine(RepositoryRoot(), FixtureRelativePath);

    /// <summary>
    /// リポジトリのルートを解決する。第一候補は**本ファイルのコンパイル時パス**（<see cref="CallerFilePathAttribute"/>）
    /// からの遡上で、CI・開発機のいずれでもチェックアウト位置に追随する。ビルドと実行が別マシンの場合に備えて
    /// 実行ディレクトリからの遡上も試し、いずれも解決できなければ**例外で落とす**（skip しない）。
    /// </summary>
    private static string RepositoryRoot([CallerFilePath] string callerFilePath = "")
    {
        var fromSource = Ascend(Path.GetDirectoryName(callerFilePath));
        if (fromSource is not null) return fromSource;

        var fromOutput = Ascend(AppContext.BaseDirectory);
        if (fromOutput is not null) return fromOutput;

        throw new InvalidOperationException(
            "リポジトリのルートを解決できませんでした（`frontend/` と `backend/` を持つ祖先が見つかりません）。"
            + $" 探索起点: `{callerFilePath}` / `{AppContext.BaseDirectory}`");
    }

    private static string? Ascend(string? start)
    {
        var dir = start;
        while (!string.IsNullOrEmpty(dir))
        {
            if (System.IO.Directory.Exists(Path.Combine(dir, "frontend"))
                && System.IO.Directory.Exists(Path.Combine(dir, "backend")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
