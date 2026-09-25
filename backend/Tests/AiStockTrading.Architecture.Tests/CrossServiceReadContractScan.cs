using System.Text.RegularExpressions;

namespace AiStockTrading.Architecture.Tests;

/// <summary>ソース 1 ファイル（サービスのディレクトリからの相対パスと原文）。</summary>
internal sealed record ContractSourceFile(string RelativePath, string Text);

/// <summary>
/// サービス 1 本の走査対象: 本番のソース・テストのソース・テストのプロジェクトファイル（別名つき参照を読む）。
/// </summary>
internal sealed record ContractServiceTree(
    string Name,
    IReadOnlyList<ContractSourceFile> Production,
    IReadOnlyList<ContractSourceFile> Tests,
    IReadOnlyList<string> TestProjectTexts);

/// <summary>
/// 検査の単位: 受け手のアダプタの<b>公開操作</b>が、<b>別サービスのルート</b>を呼び、<b>応答の本文を読む</b>。
/// </summary>
internal sealed record ReadContractUnit(string Receiver, string Adapter, string Operation, string Provider, string Route)
{
    public string Key => $"{Receiver}/{Adapter}.{Operation} -> {Provider} {Route}";
}

internal sealed record ReadContractAnalysis(
    IReadOnlyList<string> Adapters,
    IReadOnlyList<string> ProviderRoutes,
    IReadOnlyList<ReadContractUnit> Units,
    IReadOnlyList<ReadContractUnit> Unsatisfied,
    IReadOnlyList<string> SkippedWithoutBodyRead,
    IReadOnlyList<string> AdaptersWithoutRepositoryRoute,
    IReadOnlyList<string> ProvidersWithoutWireFormatTest,
    IReadOnlyList<string> RouteLiteralsOutsideAdapters);

/// <summary>
/// NFR, #952, IADR-0420: サービス間 HTTP の読み取り契約（送り手の本物の型を送り手の JSON 設定で直列化して受け手に読ませる契約テスト）を
/// <b>持たない受け手の操作</b>を、ソースの静的走査で列挙する。
/// <para>
/// <b>母集合はコードから導く。</b> 送り手のルートは各サービスの本番コードの <c>Map*</c> 呼び出し（<c>MapGroup</c> の接頭辞と結ぶ）から、
/// 受け手の単位は <c>ExternalServices/Http*.cs</c> のアダプタに現れる<b>他サービスのルートに一致する文字列リテラル</b>から得る。
/// 一覧を手で書かない（アダプタ・ルートが増減しても検査の母集合が追随する）。
/// </para>
/// <para>
/// 判定の形（IADR-0420 決定3）は試作で 3 通りを比べて決めた: ファイル単位では #943 の形（同じファイルに別の経路の契約だけがある）を、
/// メソッド単位でも「受け手の本番が別名で使う送り手の型を直列化した」形を見逃した。したがって<b>1 つのテストメソッドが操作を呼び、
/// 送り手だけが宣言し受け手の本番が参照しない型を使う</b>ことを要求する。
/// </para>
/// </summary>
internal static partial class CrossServiceReadContractScan
{
    /// <summary>実ツリー（<c>backend/Services/*</c>）を読む。</summary>
    public static IReadOnlyList<ContractServiceTree> Repository()
    {
        var servicesRoot = Path.Combine(RepositoryLayout.Root, "backend", "Services");
        return Directory.EnumerateDirectories(servicesRoot)
            .OrderBy(d => d, StringComparer.Ordinal)
            .Select(dir =>
            {
                var files = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                    .Where(RepositoryLayout.NotUnderBuildOutput)
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .Select(f => new ContractSourceFile(Path.GetRelativePath(dir, f).Replace('\\', '/'), File.ReadAllText(f)))
                    .ToList();
                var testProjects = Directory.EnumerateFiles(dir, "*.Tests.csproj", SearchOption.AllDirectories)
                    .Where(RepositoryLayout.NotUnderBuildOutput)
                    .Select(File.ReadAllText)
                    .ToList();
                return new ContractServiceTree(
                    Path.GetFileName(dir),
                    files.Where(f => !IsTestPath(f.RelativePath)).ToList(),
                    files.Where(f => IsTestPath(f.RelativePath)).ToList(),
                    testProjects);
            })
            .ToList();
    }

    // 新樹形 `<Svc>/Tests/`・旧樹形 `<Svc>/tests/` の両方をテストとして扱う（IADR-0259 の移送期間）。
    private static bool IsTestPath(string relativePath) =>
        relativePath.StartsWith("Tests/", StringComparison.Ordinal)
        || relativePath.StartsWith("tests/", StringComparison.Ordinal);

    /// <summary>アダプタ＝本番コードの <c>ExternalServices/Http*.cs</c>。</summary>
    public static bool IsAdapterPath(string relativePath) =>
        relativePath.Contains("/ExternalServices/", StringComparison.Ordinal)
        && Path.GetFileName(relativePath).StartsWith("Http", StringComparison.Ordinal)
        && relativePath.EndsWith(".cs", StringComparison.Ordinal);

    public static ReadContractAnalysis Analyze(IReadOnlyList<ContractServiceTree> services)
    {
        var parsed = services.Select(Parse).ToList();
        var routes = parsed.ToDictionary(p => p.Tree.Name, p => p.Routes, StringComparer.Ordinal);

        var units = new List<ReadContractUnit>();
        var skipped = new List<string>();
        var adapters = new List<string>();
        var withoutRoute = new List<string>();
        var stray = new List<string>();

        foreach (var service in parsed)
        {
            foreach (var file in service.Production)
            {
                var isAdapter = IsAdapterPath(file.Source.RelativePath);
                var adapterName = Path.GetFileNameWithoutExtension(file.Source.RelativePath);
                if (isAdapter) adapters.Add($"{service.Tree.Name}/{adapterName}");

                var members = isAdapter ? Members(file.Blanked, adapterName) : [];
                var matchedAny = false;
                foreach (var literal in file.Literals)
                {
                    if (file.RouteDeclarationLiterals.Contains(literal.Start)) continue;
                    var path = PathOf(literal.Text);
                    if (path is null) continue;
                    var providers = routes
                        .Where(r => r.Key != service.Tree.Name && r.Value.Any(t => Matches(path, t)))
                        .Select(r => r.Key)
                        .ToList();
                    if (providers.Count == 0) continue;

                    matchedAny = true;
                    if (!isAdapter)
                    {
                        stray.Add($"{service.Tree.Name}/{file.Source.RelativePath}: {literal.Text}");
                        continue;
                    }

                    var route = "/" + string.Join('/', path);
                    foreach (var operation in OperationsReaching(members, literal.Start))
                    {
                        if (!ReadsBody(members, operation))
                        {
                            skipped.Add($"{service.Tree.Name}/{adapterName}.{operation.Name} {route}");
                            continue;
                        }

                        units.AddRange(providers.Select(p =>
                            new ReadContractUnit(service.Tree.Name, adapterName, operation.Name, p, route)));
                    }
                }

                if (isAdapter && !matchedAny) withoutRoute.Add($"{service.Tree.Name}/{adapterName}");
            }
        }

        units = units.DistinctBy(u => u.Key).OrderBy(u => u.Key, StringComparer.Ordinal).ToList();
        var byName = parsed.ToDictionary(p => p.Tree.Name, StringComparer.Ordinal);
        var unsatisfied = units.Where(u => !Satisfied(u, byName)).ToList();

        var providersWithoutWire = units.Except(unsatisfied)
            .Select(u => u.Provider)
            .Distinct(StringComparer.Ordinal)
            .Where(p => !byName[p].Tests.Any(t => WireFormatTest().IsMatch(t.Blanked) && CreateClientCall().IsMatch(t.Blanked)))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        return new ReadContractAnalysis(
            adapters.Order(StringComparer.Ordinal).ToList(),
            routes.SelectMany(r => r.Value.Select(t => $"{r.Key} /{string.Join('/', t)}")).Order(StringComparer.Ordinal).ToList(),
            units,
            unsatisfied,
            skipped.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            withoutRoute.Order(StringComparer.Ordinal).ToList(),
            providersWithoutWire,
            stray.Order(StringComparer.Ordinal).ToList());
    }

    // ── 判定 ────────────────────────────────────────────────────────────────

    private static bool Satisfied(ReadContractUnit unit, IReadOnlyDictionary<string, ParsedService> services)
    {
        var receiver = services[unit.Receiver];
        var provider = services[unit.Provider];

        // 別名は受け手のテストのプロジェクトファイルが送り手のプロジェクトへ付けたもの（ProjectReference の Aliases）。
        var aliases = receiver.Tree.TestProjectTexts
            .SelectMany(text => AliasedReference().Matches(text))
            .Where(m => PathSegments(m.Groups["include"].Value).Contains(unit.Provider, StringComparer.Ordinal))
            .SelectMany(m => m.Groups["aliases"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
        if (aliases.Count == 0) return false;

        // 送り手の型の目印: 送り手が宣言し、受け手の本番は宣言も参照もしない型。受け手の本番が別名で使う送り手の型
        // （例: サイジング文脈の RiskLimitSettings）を数えると、受け手自身の型を直列化したテストを契約と誤認する。
        var providerOnlyTypes = provider.DeclaredTypes
            .Where(t => !receiver.DeclaredTypes.Contains(t) && !receiver.ReferencedIdentifiers.Contains(t))
            .ToHashSet(StringComparer.Ordinal);

        var operationCall = new Regex(@"\.\s*" + Regex.Escape(unit.Operation) + @"\s*[(<]");
        foreach (var test in receiver.Tests)
        {
            var declared = ExternAlias().Matches(test.Blanked).Select(m => m.Groups[1].Value);
            if (!declared.Any(a => aliases.Contains(a) && test.Blanked.Contains(a + "::", StringComparison.Ordinal))) continue;
            if (!test.Identifiers.Contains(unit.Adapter)) continue;
            if (!SerializeCall().IsMatch(test.Blanked)) continue;

            foreach (var method in TestMethods(test.Blanked))
            {
                if (!operationCall.IsMatch(method)) continue;
                if (CSharpSource.Identifiers(method).Any(providerOnlyTypes.Contains)) return true;
            }
        }

        return false;
    }

    // ── 送り手のルート ──────────────────────────────────────────────────────

    private static ParsedService Parse(ContractServiceTree tree)
    {
        var production = tree.Production.Select(ParsedFile.Of).ToList();
        var groups = new HashSet<string>(StringComparer.Ordinal);
        var leaves = new List<(bool Bare, string Template)>();

        foreach (var file in production)
        {
            foreach (Match m in MapCall().Matches(file.Blanked))
            {
                // 第 1 引数の文字列リテラル（呼び出しの開き括弧の直後に空白だけを挟んで現れるもの）。
                var literal = file.Literals.FirstOrDefault(l =>
                    l.Start >= m.Index + m.Length && file.Blanked[(m.Index + m.Length)..l.Start].Trim().Length == 0);
                if (literal.Text is null) continue;

                file.RouteDeclarationLiterals.Add(literal.Start);
                var template = literal.Text.TrimStart('$', '@').Trim('"');
                if (m.Groups["verb"].Value == "Group") groups.Add(template);
                else leaves.Add((m.Groups["receiver"].Value == "app", template));
            }
        }

        var routes = new List<string[]>();
        foreach (var (bare, template) in leaves)
        {
            if (bare) { routes.Add(Segments(template)); continue; }
            foreach (var group in groups.Where(g => g.Length > 0))
                routes.Add(Segments(group.TrimEnd('/') + "/" + template.TrimStart('/')));
        }

        return new ParsedService(
            tree,
            production,
            tree.Tests.Select(ParsedFile.Of).ToList(),
            routes.DistinctBy(r => string.Join('/', r)).ToList(),
            production.SelectMany(f => TypeDeclaration().Matches(f.Blanked).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal),
            production.SelectMany(f => f.Identifiers).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>ルートのリテラル（<c>"/a/{b}/c?x={y}"</c>）→ 区間（<c>a / * / c</c>）。ルートでなければ null。</summary>
    private static string[]? PathOf(string literalText)
    {
        var text = literalText.TrimStart('$', '@').Trim('"');
        if (text.Length < 2 || text[0] != '/' || text.Any(char.IsWhiteSpace)) return null;
        var segments = Segments(text);
        return segments.Length == 0 || segments.All(s => s == "*") ? null : segments;
    }

    private static string[] Segments(string template)
    {
        var query = template.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0) template = template[..query];
        return template.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.StartsWith('{') ? "*" : s)
            .ToArray();
    }

    private static bool Matches(string[] path, string[] route) =>
        path.Length == route.Length && path.Zip(route).All(p => p.First == p.Second || p.First == "*" || p.Second == "*");

    private static string[] PathSegments(string include) =>
        include.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.EndsWith(".csproj", StringComparison.Ordinal) ? s[..^".csproj".Length] : s)
            .ToArray();

    // ── アダプタのメンバー ──────────────────────────────────────────────────

    private sealed class Member(string name, bool isOperation, bool isType, int start, string span)
    {
        public string Name { get; } = name;
        public bool IsOperation { get; } = isOperation;
        public bool IsType { get; } = isType;
        public int Start { get; } = start;
        public string Span { get; } = span;
    }

    /// <summary>
    /// アダプタのファイルのメンバー宣言（アクセス修飾子で始まる行）。範囲は次の宣言の直前まで。
    /// 型の宣言（入れ子の DTO を含む）は範囲の境界にだけ使う。<b>操作</b>＝公開のメソッド（構築子を除く）。
    /// </summary>
    private static List<Member> Members(string blanked, string adapterName)
    {
        var raw = MemberDeclaration().Matches(blanked)
            .Select(m =>
            {
                var name = m.Groups["name"].Value;
                var isType = TypeKeyword().IsMatch(m.Groups["modifiers"].Value);
                var isMethod = m.Groups["open"].Value == "(";
                var isOperation = !isType && isMethod && m.Groups["access"].Value == "public" && name != adapterName;
                return (m.Index, name, isOperation, isType);
            })
            .ToList();

        return raw.Select((r, i) => new Member(
                r.name, r.isOperation, r.isType, r.Index, blanked[r.Index..(i + 1 < raw.Count ? raw[i + 1].Index : blanked.Length)]))
            .ToList();
    }

    /// <summary>
    /// リテラルを含むメンバーから、それを使う公開操作へ辿る（非公開のメソッド・定数に置いたルートを、参照する操作へ帰す）。
    /// </summary>
    private static IReadOnlyList<Member> OperationsReaching(List<Member> members, int position)
    {
        var enclosing = members.LastOrDefault(m => m.Start <= position);
        if (enclosing is null || enclosing.IsType) return [];

        var found = new List<Member>();
        var seen = new HashSet<Member>();
        var pending = new Stack<Member>([enclosing]);
        while (pending.Count > 0)
        {
            var member = pending.Pop();
            if (!seen.Add(member)) continue;
            if (member.IsOperation) { found.Add(member); continue; }

            var reference = new Regex(@"\b" + Regex.Escape(member.Name) + @"\b");
            foreach (var other in members.Where(o => o != member && !o.IsType))
                if (reference.IsMatch(other.Span)) pending.Push(other);
        }

        return found;
    }

    /// <summary>操作（とそこから参照するアダプタのメンバー）が応答の本文を読むか。状態コードだけを見る操作は契約の単位にしない。</summary>
    private static bool ReadsBody(List<Member> members, Member operation)
    {
        var seen = new HashSet<Member>();
        var pending = new Stack<Member>([operation]);
        while (pending.Count > 0)
        {
            var member = pending.Pop();
            if (!seen.Add(member)) continue;
            if (BodyRead().IsMatch(member.Span)) return true;

            var identifiers = CSharpSource.Identifiers(member.Span);
            foreach (var other in members.Where(o => !o.IsType && o != member && identifiers.Contains(o.Name)))
                pending.Push(other);
        }

        return false;
    }

    // ── テストメソッド ──────────────────────────────────────────────────────

    /// <summary><c>[Fact]</c> / <c>[Theory]</c> の付いたメソッドの範囲（属性から本体の閉じ括弧まで）。</summary>
    private static IEnumerable<string> TestMethods(string blanked)
    {
        foreach (Match m in TestAttribute().Matches(blanked))
        {
            var brace = blanked.IndexOf('{', m.Index);
            var arrow = blanked.IndexOf("=>", m.Index, StringComparison.Ordinal);
            if (brace < 0 && arrow < 0) continue;

            if (arrow >= 0 && (brace < 0 || arrow < brace))
            {
                var semicolon = blanked.IndexOf(';', arrow);
                yield return blanked[m.Index..(semicolon < 0 ? blanked.Length : semicolon + 1)];
                continue;
            }

            var depth = 0;
            var i = brace;
            for (; i < blanked.Length; i++)
            {
                if (blanked[i] == '{') depth++;
                else if (blanked[i] == '}' && --depth == 0) break;
            }

            yield return blanked[m.Index..Math.Min(i + 1, blanked.Length)];
        }
    }

    private sealed class ParsedFile
    {
        public required ContractSourceFile Source { get; init; }
        public required string Blanked { get; init; }
        public required IReadOnlyList<(int Start, string Text)> Literals { get; init; }
        public required IReadOnlySet<string> Identifiers { get; init; }
        public HashSet<int> RouteDeclarationLiterals { get; } = [];

        public static ParsedFile Of(ContractSourceFile source)
        {
            var blanked = CSharpSource.BlankCommentsAndLiterals(source.Text);
            return new ParsedFile
            {
                Source = source,
                Blanked = blanked,
                Literals = CSharpSource.StringLiterals(source.Text),
                Identifiers = CSharpSource.Identifiers(blanked),
            };
        }
    }

    private sealed record ParsedService(
        ContractServiceTree Tree,
        IReadOnlyList<ParsedFile> Production,
        IReadOnlyList<ParsedFile> Tests,
        IReadOnlyList<string[]> Routes,
        IReadOnlySet<string> DeclaredTypes,
        IReadOnlySet<string> ReferencedIdentifiers);

    [GeneratedRegex(@"(?<receiver>[A-Za-z_]\w*)\s*\.\s*Map(?<verb>Group|Get|Post|Put|Delete|Patch)\s*\(")]
    private static partial Regex MapCall();

    [GeneratedRegex(@"<ProjectReference\s+Include=""(?<include>[^""]+)""[^>]*?\bAliases=""(?<aliases>[^""]+)""")]
    private static partial Regex AliasedReference();

    [GeneratedRegex(@"^\s*extern\s+alias\s+([A-Za-z_]\w*)\s*;", RegexOptions.Multiline)]
    private static partial Regex ExternAlias();

    [GeneratedRegex(@"\bJsonSerializer\s*\.\s*Serialize\w*\s*[<(]")]
    private static partial Regex SerializeCall();

    [GeneratedRegex(@"\bJsonNode\s*\.\s*DeepEquals\s*\(")]
    private static partial Regex WireFormatTest();

    [GeneratedRegex(@"\.\s*CreateClient\s*\(")]
    private static partial Regex CreateClientCall();

    [GeneratedRegex(@"\b(?:class|record|struct|enum|interface)\s+(?!(?:class|struct)\b)([A-Za-z_]\w*)")]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex(@"\b(?:class|record|struct|enum|interface)\b")]
    private static partial Regex TypeKeyword();

    [GeneratedRegex(
        @"^[ \t]*(?<access>public|private|internal|protected)\b(?<modifiers>[^;{}=\n(]*?)\b(?<name>[A-Za-z_]\w*)\s*(?<open>\(|=(?!>)|$)",
        RegexOptions.Multiline)]
    private static partial Regex MemberDeclaration();

    [GeneratedRegex(@"\[\s*(?:Fact|Theory)\b")]
    private static partial Regex TestAttribute();

    [GeneratedRegex(
        @"\b(?:ReadFromJsonAsync|ReadAsStringAsync|ReadAsStreamAsync|ReadAsByteArrayAsync|GetFromJsonAsync|GetStringAsync|GetStreamAsync"
        + @"|GetByteArrayAsync|Deserialize|DeserializeAsync)\b|\bJson(?:Document|Node)\s*\.\s*Parse\b")]
    private static partial Regex BodyRead();
}
