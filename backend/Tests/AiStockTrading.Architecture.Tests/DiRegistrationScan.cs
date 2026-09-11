using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AiStockTrading.Architecture.Tests;

/// <summary>走査対象のソース 1 件（パスと本文）。</summary>
/// <param name="Path">失敗メッセージに出すパス（リポジトリルートからの相対）。</param>
/// <param name="Text">ソース本文。</param>
internal sealed record ScannedSource(string Path, string Text);

/// <summary>
/// 走査対象のプロジェクト 1 件。<b>実ツリーからも合成ソースからも作れる</b>形にしてある
/// （自己試験が同じ判定器を通るようにするため）。
/// </summary>
/// <param name="Name">プロジェクト名（＝アセンブリ名。本リポジトリでは csproj のファイル名と一致）。</param>
/// <param name="Sources">そのプロジェクトが持つ <c>.cs</c>。</param>
/// <param name="ProjectReferences">参照先プロジェクトの<b>名前</b>。</param>
internal sealed record ScannedProject(
    string Name,
    IReadOnlyList<ScannedSource> Sources,
    IReadOnlyList<string> ProjectReferences);

/// <summary>DI 登録 1 件。</summary>
/// <param name="ProjectName">登録が書かれたプロジェクト。</param>
/// <param name="TypeName">登録された<b>サービス型</b>（型引数の 1 つ目）の単純名。</param>
/// <param name="Kind"><c>AddScoped</c> / <c>AddSingleton</c> / <c>AddTransient</c> / <c>AddHostedService</c>。</param>
/// <param name="SourcePath">登録が書かれたファイル。</param>
/// <param name="Line">登録が書かれた行（1 始まり）。</param>
internal sealed record DiRegistration(string ProjectName, string TypeName, string Kind, string SourcePath, int Line)
{
    /// <summary>許容リストの照合キー（<c>プロジェクト名/型名</c>）。</summary>
    public string Key => $"{ProjectName}/{TypeName}";

    /// <summary>失敗メッセージ用の 1 行表現。</summary>
    public string Describe() => $"{Key}（{Kind} @ {SourcePath}:{Line}）";
}

/// <summary>走査の結果。</summary>
/// <param name="Registrations">走査できた DI 登録の全件。</param>
/// <param name="Projects">走査したプロジェクト。</param>
/// <param name="WithoutProductionUse">本番の利用箇所が 1 件も無い登録（<b>種別で除外する前</b>の生の判定）。</param>
internal sealed record DiRegistrationAnalysis(
    IReadOnlyList<DiRegistration> Registrations,
    IReadOnlyList<ScannedProject> Projects,
    IReadOnlyList<DiRegistration> WithoutProductionUse);

/// <summary>
/// NFR, #752, #204 C-3-b, IADR-0335:
/// <b>「DI に登録されているが、本番の利用箇所が 1 件も無い型」</b>をソースの静的走査で列挙する。
/// <para>
/// 2026-09-02 の go-live 前監査は、この形の欠陥を <b>D-1（Stage 0 判定）と D-3（維持率割れの自動縮小）で
/// 2 回</b>観測した。「型は在る・テストも通る・しかし本番から呼ばれない」ため <b>CI は 1 つも赤くならない</b>。
/// 運用規約「検査器・規約の追加は同型の事故が 2 回起きたら」に達したため本走査を置く。
/// </para>
/// <para>
/// 🔴 <b>判定するのはサービス型（型引数の 1 つ目）であって、実装型ではない。</b>
/// <c>AddScoped&lt;IFoo, Foo&gt;()</c> の <c>Foo</c> は、生成を DI に委ねる以上
/// <b>自ファイル以外から参照されないのが正常な形</b>である（それが DI を使う理由そのものである）。
/// 実装型を判定対象にすると実測でほぼ全件が違反になり、許容リストが本体より長くなって検査が外される。
/// <b>止めたいのは「解決キーを登録したのに、それを解決する者が誰も居ない」</b>ほうである。
/// </para>
/// <para>
/// 🔴 <b>リフレクションは採らない</b>（IADR-0128 決定 6 と同じ理由）。被検査アセンブリを読み込む形にすると
/// アーキテクチャテストが全サービスを <c>ProjectReference</c> することになり、しかも
/// <b>未使用の参照を検出できず、最適化で消えた参照も見逃す</b>。
/// </para>
/// </summary>
internal static class DiRegistrationScan
{
    /// <summary>走査する DI 登録の種別。</summary>
    public static readonly string[] RegistrationKinds =
        ["AddScoped", "AddSingleton", "AddTransient", "AddHostedService"];

    /// <summary>
    /// <b>判定から外す種別</b>: <c>AddHostedService&lt;T&gt;</c>。
    /// <c>IHostedService</c> はホストが解決して起動する契約であり、<b>呼び出し元がゼロであることが正しい形</b>である
    /// （実測: 本番の常駐サービス 16 件はすべて利用箇所ゼロ）。ここを違反にすると検査は最初の日に外される。
    /// <b>走査はする</b>（総数の下限検査に効かせる）が判定はしない。
    /// </summary>
    public const string HostResolvedKind = "AddHostedService";

    private static readonly Regex RegistrationHead = new(
        @"\.(AddScoped|AddSingleton|AddTransient|AddHostedService)\s*<", RegexOptions.Compiled);

    private static readonly ConcurrentDictionary<string, Regex> DeclarationPatterns = new(StringComparer.Ordinal);

    /// <summary>合成ソース・実ツリーのどちらに対しても同じ判定を行う（自己試験が本番と同じ経路を通る）。</summary>
    public static DiRegistrationAnalysis Analyze(IReadOnlyList<ScannedProject> projects)
    {
        var analyzed = projects.ToDictionary(
            p => p.Name,
            p => p.Sources.Select(s => new AnalyzedSource(s.Path, s.Text)).ToArray(),
            StringComparer.Ordinal);

        var registrations = new List<DiRegistration>();
        var unwired = new List<DiRegistration>();

        foreach (var project in projects)
        {
            var scope = TransitiveScope(project, projects)
                .SelectMany(name => analyzed[name])
                .ToArray();

            var own = analyzed[project.Name]
                .SelectMany(source => source.Registrations.Select(
                    r => new DiRegistration(project.Name, r.TypeName, r.Kind, source.Path, r.Line)))
                .ToArray();

            registrations.AddRange(own);

            foreach (var group in own.GroupBy(r => r.TypeName, StringComparer.Ordinal))
            {
                if (!HasProductionUse(group.Key, scope)) unwired.AddRange(group);
            }
        }

        return new DiRegistrationAnalysis(
            [.. registrations.OrderBy(r => r.Key, StringComparer.Ordinal)],
            projects,
            [.. unwired.OrderBy(r => r.Key, StringComparer.Ordinal)]);
    }

    /// <summary>実ツリー（本番プロジェクトのみ）から走査対象を組む。</summary>
    public static IReadOnlyList<ScannedProject> Production()
    {
        var production = RepositoryLayout.ProductionProjectFiles.Select(ProjectFile.Load).ToArray();
        var namesByPath = production.ToDictionary(p => p.Path, p => p.Name, StringComparer.OrdinalIgnoreCase);

        return production
            .Select(p => new ScannedProject(
                p.Name,
                OwnedSources(p.Path),
                [.. p.ProjectReferences
                    .Where(namesByPath.ContainsKey)
                    .Select(r => namesByPath[r])]))
            .ToArray();
    }

    /// <summary>
    /// そのプロジェクトが<b>コンパイルする</b> <c>.cs</c>（SDK 形式の既定のとおり、直下から再帰）。
    /// <b>入れ子のプロジェクトディレクトリ（<c>&lt;Svc&gt;/Tests/</c>）は親の持ち物ではない</b>ため除く。
    /// </summary>
    private static IReadOnlyList<ScannedSource> OwnedSources(string projectPath)
    {
        var directory = Path.GetDirectoryName(projectPath)!;
        var nested = RepositoryLayout.AllProjectFiles
            .Select(Path.GetDirectoryName)
            .OfType<string>()
            .Where(d => !string.Equals(d, directory, StringComparison.OrdinalIgnoreCase)
                && d.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(RepositoryLayout.NotUnderBuildOutput)
            .Where(f => !f.EndsWith(".g.cs", StringComparison.Ordinal))
            .Where(f => !nested.Any(n => f.StartsWith(n + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .Select(f => new ScannedSource(
                Path.GetRelativePath(RepositoryLayout.Root, f).Replace('\\', '/'),
                File.ReadAllText(f)))
            .OrderBy(s => s.Path, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 利用箇所を探す範囲 = 登録が書かれたプロジェクト ∪ その推移的な参照先。
    /// <para>
    /// 🔴 <b>リポジトリ全体を範囲にしない。</b> 別サービスの同名型が影を作り、未結線を見落とす
    /// （実測: <c>IClock</c> は 9 サービスにそれぞれ別々に宣言されている）。
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> TransitiveScope(ScannedProject start, IReadOnlyList<ScannedProject> all)
    {
        var byName = all.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { start.Name };
        var pending = new Stack<string>([start.Name]);
        while (pending.Count > 0)
        {
            if (!byName.TryGetValue(pending.Pop(), out var current)) continue;
            foreach (var reference in current.ProjectReferences)
                if (byName.ContainsKey(reference) && seen.Add(reference)) pending.Push(reference);
        }

        return [.. seen];
    }

    /// <summary>
    /// その型に<b>本番の利用箇所</b>があるか。<b>宣言ファイル自身の中の言及は「利用」ではない</b>
    /// （型は自分の名前を必ず含む）。
    /// </summary>
    private static bool HasProductionUse(string typeName, IReadOnlyList<AnalyzedSource> scope)
    {
        var declaration = DeclarationPatterns.GetOrAdd(
            typeName,
            n => new Regex($@"\b(?:class|record|struct|interface|enum)\s+@?{Regex.Escape(n)}\b", RegexOptions.Compiled));

        var declaring = scope.Where(s => declaration.IsMatch(s.Code)).Select(s => s.Path).ToHashSet(StringComparer.Ordinal);

        return scope.Any(source => !declaring.Contains(source.Path) && source.Uses(typeName));
    }

    /// <summary>1 ファイルぶんの前処理済みソース。</summary>
    private sealed class AnalyzedSource
    {
        public AnalyzedSource(string path, string text)
        {
            Path = path;
            Code = CSharpSource.BlankCommentsAndLiterals(text);
            Aliases = CSharpSource.UsingAliases(Code);

            var (registrations, spans) = ExtractRegistrations(Code, Aliases);
            Registrations = registrations;

            // 🔴 潰す順序ではなく「長さを保つ」ことが要点である（オフセットがずれない）。
            Identifiers = CSharpSource.Identifiers(
                CSharpSource.BlankUsingDirectives(CSharpSource.BlankRanges(Code, spans)));
        }

        public string Path { get; }

        /// <summary>コメント・リテラルを潰したソース（宣言の探索に使う）。</summary>
        public string Code { get; }

        public IReadOnlyDictionary<string, string> Aliases { get; }

        public IReadOnlyList<(string Kind, string TypeName, int Line)> Registrations { get; }

        /// <summary>DI 登録式・<c>using</c> 行を除いた本文に現れる識別子。</summary>
        private IReadOnlySet<string> Identifiers { get; }

        /// <summary>
        /// このファイルがその型を使っているか。<b>ファイル内の using エイリアス経由の利用も数える</b>
        /// —— 登録側が実名・利用側が別名という書き方が実在し、片方向だけ解決すると偽陽性が出る。
        /// </summary>
        public bool Uses(string typeName)
        {
            if (Identifiers.Contains(typeName)) return true;
            foreach (var (alias, target) in Aliases)
                if (string.Equals(target, typeName, StringComparison.Ordinal) && Identifiers.Contains(alias)) return true;
            return false;
        }

        private static (IReadOnlyList<(string Kind, string TypeName, int Line)>, IReadOnlyList<(int, int)>)
            ExtractRegistrations(string code, IReadOnlyDictionary<string, string> aliases)
        {
            var found = new List<(string, string, int)>();
            var spans = new List<(int, int)>();

            foreach (Match head in RegistrationHead.Matches(code))
            {
                var open = head.Index + head.Length;   // `<` の次
                var depth = 1;
                var i = open;
                while (i < code.Length && depth > 0)
                {
                    var c = code[i];
                    if (c == '<') depth++;
                    else if (c == '>') depth--;
                    else if (c is ';' or '{') break;   // 総称でない（比較演算子など）ため打ち切る
                    i++;
                }

                if (depth != 0) continue;

                spans.Add((head.Index, i));

                var arguments = CSharpSource.SplitTypeArguments(code[open..(i - 1)]);
                if (arguments.Count == 0) continue;

                var serviceType = CSharpSource.SimpleTypeName(arguments[0]);
                if (aliases.TryGetValue(serviceType, out var resolved)) serviceType = resolved;
                if (!CSharpSource.IsSimpleIdentifier(serviceType)) continue;

                found.Add((head.Groups[1].Value, serviceType, LineOf(code, head.Index)));
            }

            return (found, spans);
        }

        private static int LineOf(string code, int index)
        {
            var line = 1;
            for (var i = 0; i < index; i++) if (code[i] == '\n') line++;
            return line;
        }
    }
}
