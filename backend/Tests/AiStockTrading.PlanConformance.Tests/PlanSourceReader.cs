using System.Security.Cryptography;
using System.Text;

namespace AiStockTrading.PlanConformance.Tests;

/// <summary>
/// NFR, #378, IADR-0166: 計画書（planning submodule）の**節**を読み、正規化してダイジェストを取る。
/// <para>
/// <b>値の抽出は行わない。</b> 散文からの値抽出（IADR-0166 の案 B）は、抽出に失敗したときに
/// 「差分なし」＝緑に見える形で壊れる。それは本機構が塞ごうとしている穴そのものである。
/// ダイジェストは壊れれば必ず赤になる。
/// </para>
/// </summary>
public static class PlanSourceReader
{
    /// <summary>
    /// planning submodule のルート（`&lt;repo&gt;/planning`）。**未 populate なら <c>null</c>**。
    /// テストのカレントは <c>bin/Debug/net10.0</c> なのでリポジトリルートまで遡る。
    /// </summary>
    public static string? TryFindPlanningRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "planning");
            // submodule が未取得のときディレクトリは存在するが**空**である。中身の有無で判定する。
            if (Directory.Exists(candidate) && Directory.EnumerateFileSystemEntries(candidate).Any())
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// 見出し行から、<b>同位以上の次の見出し</b>の直前までを 1 節として切り出す（見出し行を含む）。
    /// 節が見つからなければ <c>null</c>。
    /// <para>
    /// 見出しは<b>前方一致</b>で探す（例: <c>"### 1."</c>）。見出しの表題そのものは節の本文として
    /// ハッシュに含めるため、表題の変更は検知される。前方一致にするのは**節を見つけるため**であって、
    /// 表題の変更を見逃すためではない。
    /// </para>
    /// </summary>
    public static string? TryExtractSection(string markdown, string headingPrefix)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(headingPrefix);

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var level = headingPrefix.TakeWhile(c => c == '#').Count();
        if (level == 0)
        {
            throw new ArgumentException("見出しの前方一致は '#' で始めること。", nameof(headingPrefix));
        }

        var start = Array.FindIndex(lines, l => l.StartsWith(headingPrefix, StringComparison.Ordinal));
        if (start < 0)
        {
            return null;
        }

        var end = lines.Length;
        for (var i = start + 1; i < lines.Length; i++)
        {
            var hashes = lines[i].TakeWhile(c => c == '#').Count();
            // 同位（同じ深さ）以上（より浅い）の見出しで打ち切る。`#### ` のような下位見出しは節の一部。
            if (hashes is > 0 && hashes <= level && lines[i].Length > hashes && lines[i][hashes] == ' ')
            {
                end = i;
                break;
            }
        }

        return string.Join('\n', lines[start..end]);
    }

    /// <summary>
    /// 正規化（IADR-0166 決定3）: 改行コードを LF へ、各行の**行末空白**を落とし、末尾の空行を落とす。
    /// <para>
    /// <b>これ以上の正規化は行わない。</b> 全角/半角の吸収や表の整形まで正規化すると、
    /// <b>内容の変化まで吸収し得る</b>（＝検知漏れ）。行末空白と改行コードは計画の内容ではないため、
    /// 赤にする価値がない——それだけを落とす。
    /// </para>
    /// </summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(l => l.TrimEnd())
            .ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join('\n', lines);
    }

    /// <summary>正規化済みテキストの SHA-256（小文字 16 進）。</summary>
    public static string Sha256Hex(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    /// <summary>
    /// 1 件のダイジェストを実際の計画書から計算する。ファイルが無い／節が無い場合は
    /// それぞれを表す結果を返す（<b>例外にしない</b>——テスト側が「何が起きたか」を文言で出すため）。
    /// </summary>
    public static PlanSourceProbe Probe(string planningRoot, PlanSourceDigest expected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planningRoot);
        ArgumentNullException.ThrowIfNull(expected);

        var path = Path.Combine(planningRoot, expected.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            return new PlanSourceProbe(expected, null, $"計画書が見つからない: {expected.RelativePath}");
        }

        var section = TryExtractSection(File.ReadAllText(path), expected.HeadingPrefix);
        if (section is null)
        {
            return new PlanSourceProbe(
                expected,
                null,
                $"節が見つからない: {expected.RelativePath} の「{expected.HeadingPrefix}」"
                    + "（計画側で見出しが改名・再編された可能性がある）");
        }

        return new PlanSourceProbe(expected, Sha256Hex(Normalize(section)), null);
    }
}

/// <summary>計画書の 1 節を実測した結果。<paramref name="Actual"/> が <c>null</c> なら <paramref name="Problem"/> に理由が入る。</summary>
public sealed record PlanSourceProbe(PlanSourceDigest Expected, string? Actual, string? Problem)
{
    public bool Matches => Actual is not null && Actual == Expected.Sha256;
}
