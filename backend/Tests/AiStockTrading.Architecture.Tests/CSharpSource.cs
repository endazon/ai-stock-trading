using System.Text.RegularExpressions;

namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// NFR, IADR-0335: C# ソースを「構文の静的走査」で読むための最小の前処理。
/// <para>
/// 本リポジトリのアーキテクチャテストは<b>被検査プロジェクトを <c>ProjectReference</c> しない</b>
/// （IADR-0128 決定 6）。したがって型の参照関係もソースの文字列から読むほかなく、
/// <b>コメント・文字列リテラルを先に潰しておかないと「言及」と「利用」を取り違える</b>。
/// </para>
/// <para>
/// 🔴 <b>潰す操作はすべて「長さを保つ」</b>（文字を空白へ置き換え、改行だけ残す）。行や範囲を
/// 削除する実装にすると、あとから別の範囲を潰すときに<b>オフセットがずれて無関係な場所を消す</b>
/// —— 本検査の試作段階で実際にこれを踏み、DI 登録行の残骸が「利用箇所」と数えられて
/// <b>検出したい当のもの（維持率割れの自動縮小）を取りこぼした</b>。
/// </para>
/// </summary>
internal static class CSharpSource
{
    /// <summary>行頭の <c>using</c> ディレクティブ（<c>global using</c> を含む）。</summary>
    private static readonly Regex UsingDirectiveLine = new(
        @"^[ \t]*(?:global[ \t]+)?using[ \t][^\n]*$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>using エイリアス（<c>using AppSvc = Foo.Bar.CostControlAppService;</c>）。</summary>
    private static readonly Regex UsingAliasLine = new(
        @"^[ \t]*(?:global[ \t]+)?using[ \t]+(?:unsafe[ \t]+)?([A-Za-z_]\w*)[ \t]*=[ \t]*([^;]+);",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>識別子トークン。</summary>
    private static readonly Regex Identifier = new(@"[A-Za-z_]\w*", RegexOptions.Compiled);

    /// <summary>
    /// コメント（行・ブロック）と文字列 / 文字リテラルを空白へ潰す。<b>文字列長は変わらない</b>。
    /// 逐語（<c>@"..."</c>）・生（<c>"""..."""</c>）・補間（<c>$"..."</c>）のいずれも 1 つの塊として潰す。
    /// </summary>
    public static string BlankCommentsAndLiterals(string source)
    {
        var buffer = source.ToCharArray();
        var i = 0;
        while (i < buffer.Length)
        {
            var c = buffer[i];
            if (c == '/' && i + 1 < buffer.Length && buffer[i + 1] == '/') { i = BlankToEndOfLine(buffer, i); continue; }
            if (c == '/' && i + 1 < buffer.Length && buffer[i + 1] == '*') { i = BlankBlockComment(buffer, i); continue; }
            if (c == '"') { i = BlankStringLiteral(buffer, i); continue; }
            if (c == '\'') { i = BlankCharLiteral(buffer, i); continue; }
            i++;
        }

        return new string(buffer);
    }

    /// <summary>指定した範囲を空白へ潰す（改行は残す）。<b>長さは変えない</b>。</summary>
    public static string BlankRanges(string source, IEnumerable<(int Start, int End)> ranges)
    {
        var buffer = source.ToCharArray();
        foreach (var (start, end) in ranges) Blank(buffer, start, Math.Min(end, buffer.Length));
        return new string(buffer);
    }

    /// <summary>
    /// <c>using</c> ディレクティブの行を空白へ潰す。<b>名前空間の並びは型の「利用」ではない</b>
    /// —— 潰さないと、登録しただけで誰も呼ばない型が <c>Program.cs</c> の <c>using</c> 1 行で
    /// 「利用あり」になる。
    /// </summary>
    public static string BlankUsingDirectives(string source) =>
        UsingDirectiveLine.Replace(source, m => new string(' ', m.Length));

    /// <summary>
    /// ファイル内の using エイリアス（<c>別名 → 単純型名</c>）。
    /// <b>登録側が実名・利用側が別名</b>という書き方が実在する（<c>CostControlAppService</c> ほか 3 件）。
    /// </summary>
    public static IReadOnlyDictionary<string, string> UsingAliases(string blankedSource)
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in UsingAliasLine.Matches(blankedSource))
        {
            var target = SimpleTypeName(m.Groups[2].Value);
            if (target.Length > 0) aliases[m.Groups[1].Value] = target;
        }

        return aliases;
    }

    /// <summary>ソースに現れる識別子トークンの集合（利用判定の索引）。</summary>
    public static IReadOnlySet<string> Identifiers(string blankedSource)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Identifier.Matches(blankedSource)) set.Add(m.Value);
        return set;
    }

    /// <summary>
    /// 型式（<c>global::Foo.Bar&lt;T&gt;</c>）から単純型名（<c>Bar</c>）を取り出す。
    /// 名前空間・総称引数・<c>global::</c> を落とす。
    /// </summary>
    public static string SimpleTypeName(string typeExpression)
    {
        var s = typeExpression.Trim();
        var angle = s.IndexOf('<');
        if (angle >= 0) s = s[..angle];
        var dot = s.LastIndexOf('.');
        if (dot >= 0) s = s[(dot + 1)..];
        return s.Trim().TrimStart('@');
    }

    /// <summary>その識別子が単純型名として妥当か（総称・配列・タプル等を弾く）。</summary>
    public static bool IsSimpleIdentifier(string value) =>
        value.Length > 0 && (char.IsLetter(value[0]) || value[0] == '_') && value.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>
    /// 総称引数リストを最上位のカンマで割る（<c>IFoo&lt;A,B&gt;, Foo</c> → <c>IFoo&lt;A,B&gt;</c> / <c>Foo</c>）。
    /// </summary>
    public static IReadOnlyList<string> SplitTypeArguments(string arguments)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case '<': depth++; break;
                case '>': depth--; break;
                case ',' when depth == 0:
                    parts.Add(arguments[start..i]);
                    start = i + 1;
                    break;
            }
        }

        if (start < arguments.Length) parts.Add(arguments[start..]);
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
    }

    private static int BlankToEndOfLine(char[] buffer, int start)
    {
        var i = start;
        while (i < buffer.Length && buffer[i] != '\n') buffer[i++] = ' ';
        return i;
    }

    private static int BlankBlockComment(char[] buffer, int start)
    {
        var i = start + 2;
        while (i < buffer.Length && !(buffer[i] == '*' && i + 1 < buffer.Length && buffer[i + 1] == '/')) i++;
        var end = Math.Min(i + 2, buffer.Length);
        Blank(buffer, start, end);
        return end;
    }

    private static int BlankCharLiteral(char[] buffer, int start)
    {
        var i = start + 1;
        while (i < buffer.Length && buffer[i] != '\n')
        {
            if (buffer[i] == '\\') { i += 2; continue; }
            if (buffer[i] == '\'') { i++; break; }
            i++;
        }

        var end = Math.Min(i, buffer.Length);
        Blank(buffer, start, end);
        return end;
    }

    private static int BlankStringLiteral(char[] buffer, int start)
    {
        var quotes = 0;
        while (start + quotes < buffer.Length && buffer[start + quotes] == '"') quotes++;

        int end;
        if (quotes >= 3)
        {
            // 生文字列リテラル: 同数以上の `"` の並びで閉じる。
            end = FindRawStringEnd(buffer, start + quotes, quotes);
        }
        else if (quotes == 2)
        {
            // 空文字列（逐語の `@""` を含む）。
            end = start + 2;
        }
        else if (IsVerbatim(buffer, start))
        {
            end = FindVerbatimEnd(buffer, start + 1);
        }
        else
        {
            end = FindRegularStringEnd(buffer, start + 1);
        }

        end = Math.Min(end, buffer.Length);
        Blank(buffer, start, end);
        return end;
    }

    /// <summary>直前が <c>@</c>（<c>$@</c> / <c>@$</c> を含む）なら逐語文字列である。</summary>
    private static bool IsVerbatim(char[] buffer, int quoteIndex)
    {
        for (var i = quoteIndex - 1; i >= 0 && (buffer[i] == '@' || buffer[i] == '$'); i--)
            if (buffer[i] == '@') return true;
        return false;
    }

    private static int FindRawStringEnd(char[] buffer, int from, int quotes)
    {
        var i = from;
        while (i < buffer.Length)
        {
            if (buffer[i] != '"') { i++; continue; }
            var run = 0;
            while (i + run < buffer.Length && buffer[i + run] == '"') run++;
            if (run >= quotes) return i + run;
            i += run;
        }

        return buffer.Length;
    }

    private static int FindVerbatimEnd(char[] buffer, int from)
    {
        var i = from;
        while (i < buffer.Length)
        {
            if (buffer[i] != '"') { i++; continue; }
            if (i + 1 < buffer.Length && buffer[i + 1] == '"') { i += 2; continue; }
            return i + 1;
        }

        return buffer.Length;
    }

    private static int FindRegularStringEnd(char[] buffer, int from)
    {
        var i = from;
        while (i < buffer.Length && buffer[i] != '\n')
        {
            if (buffer[i] == '\\') { i += 2; continue; }
            if (buffer[i] == '"') return i + 1;
            i++;
        }

        return Math.Min(i, buffer.Length);
    }

    private static void Blank(char[] buffer, int start, int end)
    {
        for (var i = Math.Max(start, 0); i < end; i++)
            if (buffer[i] != '\n') buffer[i] = ' ';
    }
}
