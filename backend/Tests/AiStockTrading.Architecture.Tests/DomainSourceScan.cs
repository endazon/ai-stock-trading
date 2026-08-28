using System.Text.RegularExpressions;

namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// NFR, platform ADR-0030 §基本方針, IADR-0256:
/// Domain 層の依存規律を<b>ソースの静的走査</b>で判定するための照合器群。
/// <para>
/// <b>すべて純関数として切り出してある。</b> 実ツリーの違反は現時点で 0 件であり、
/// 照合器が常に「違反なし」を返すよう壊れても実ツリー走査のテストは緑のままになる。
/// 照合器そのものが load-bearing であることを対のテストで固定できるようにするための形である
/// （本アセンブリの既存テストが同じ作法を採っている——照合器を純関数として切り出し、
/// 肯定・否定の両方を <c>[Theory]</c> で固定する形である）。
/// </para>
/// </summary>
internal static class DomainSourceScan
{
    /// <summary>
    /// <c>using</c> ディレクティブの解析。<c>global using</c> / <c>using static</c> / エイリアス
    /// （<c>using A = X.Y;</c>）を受け付け、<b>using 文</b>（<c>using var db = ...;</c> /
    /// <c>using (var scope = ...)</c>）は名前空間として解析しない。
    /// </summary>
    private static readonly Regex UsingDirective = new(
        @"^\s*(?:global\s+)?using\s+(?:static\s+)?(?:[A-Za-z_][A-Za-z0-9_]*\s*=\s*)?"
            + @"(?<ns>[A-Za-z_][A-Za-z0-9_]*(?:\s*\.\s*[A-Za-z_][A-Za-z0-9_]*)*)\s*;\s*$",
        RegexOptions.Compiled);

    /// <summary>本ユニットの名前空間の接頭辞。</summary>
    public const string UnitNamespace = "AiStockTrading";

    /// <summary>1 行を <c>using</c> ディレクティブとして解析する。解析できたら名前空間を返す。</summary>
    public static bool TryParseUsingNamespace(string line, out string ns)
    {
        var match = UsingDirective.Match(line);
        if (!match.Success)
        {
            ns = string.Empty;
            return false;
        }

        // `using System . Text ;` のような空白入りも正規化して返す。
        ns = Regex.Replace(match.Groups["ns"].Value, @"\s+", string.Empty);

        // `using var x;` は「名前空間 var を import」ではなく using 宣言である。
        // 単一セグメントの `var` だけを除く（`var` は文脈キーワードであり名前空間名には使えない）。
        if (ns == "var")
        {
            ns = string.Empty;
            return false;
        }

        return true;
    }

    /// <summary>ソース全体から <c>using</c> ディレクティブの名前空間を列挙する（出現順・重複を残す）。</summary>
    public static IReadOnlyList<string> UsingNamespacesIn(string sourceText)
    {
        var found = new List<string>();
        foreach (var line in sourceText.Split('\n'))
        {
            if (TryParseUsingNamespace(line.TrimEnd('\r'), out var ns)) found.Add(ns);
        }

        return found;
    }

    /// <summary>
    /// 検査 (b): Domain が <c>using</c> してよい名前空間か。<b>許可リストである（fail-closed）。</b>
    /// <list type="bullet">
    ///   <item><c>System[.*]</c>: .NET 標準（platform ADR-0030 §基本方針が Domain に許す唯一の外部）</item>
    ///   <item><c>AiStockTrading.&lt;任意&gt;.Domain[.*]</c>: 他の Domain（旧 csproj 方式の許可リスト
    ///     <c>*.Domain</c> と同じ広さ。自サービスかどうかは検査 (d) が別に絞る）</item>
    ///   <item><c>AiStockTrading.Shared.Contracts[.*]</c>: ユニット単位の契約（platform ADR-0019 決定 4）</item>
    ///   <item><c>AiStockTrading.Shared.Kernel[.*]</c>: 共有カーネル（IADR-0260 で新設）</item>
    /// </list>
    /// <c>AiStockTrading.Shared.Infrastructure</c> / <c>.KnowledgeBase</c> は<b>許可しない</b>
    /// （外部ライブラリを引き込む実装であり、Domain から到達できてはならない）。
    /// </summary>
    public static bool IsAllowedDomainNamespace(string ns)
    {
        if (IsOrStartsWith(ns, "System")) return true;
        if (IsOrStartsWith(ns, $"{UnitNamespace}.Shared.Contracts")) return true;
        if (IsOrStartsWith(ns, $"{UnitNamespace}.Shared.Kernel")) return true;

        var segments = ns.Split('.');
        return segments.Length >= 3
            && segments[0] == UnitNamespace
            && segments[2] == "Domain";
    }

    private static bool IsOrStartsWith(string ns, string prefix) =>
        ns == prefix || ns.StartsWith(prefix + ".", StringComparison.Ordinal);

    /// <summary>
    /// 検査 (d): ソース中に現れる<b>他サービスの名前空間の根</b>（<c>AiStockTrading.&lt;Other&gt;</c>）。
    /// <para>
    /// <c>using</c> 行に限らず全文を走査する。完全修飾（<c>ConfigurationService.Domain.CostCalculator</c>）で
    /// 書けば <c>using</c> 走査を迂回できてしまうためである。
    /// </para>
    /// <c>AiStockTrading.Shared.*</c> は共有物であり他サービスではないので除く。
    /// </summary>
    public static IReadOnlyList<string> ForeignServiceReferencesIn(string sourceText, string ownServiceShortName)
    {
        var pattern = new Regex(
            @"(?<![A-Za-z0-9_.])" + UnitNamespace + @"\.(?<svc>[A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.None);

        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in pattern.Matches(sourceText))
        {
            var service = match.Groups["svc"].Value;
            if (service == "Shared" || service == ownServiceShortName) continue;
            found.Add($"{UnitNamespace}.{service}");
        }

        return found.ToArray();
    }

    /// <summary>
    /// 検査 (c) の母集合その 1: <c>Directory.Packages.props</c> の <c>PackageVersion Include=</c>。
    /// <b>拒否リストを手で書かない</b>——次に足されたパッケージが素通りするからである。
    /// </summary>
    public static IReadOnlyList<string> CentralPackageIds(string packagesPropsText) =>
        Regex.Matches(packagesPropsText, @"<PackageVersion\s+Include=""(?<id>[^""]+)""")
            .Select(m => m.Groups["id"].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// パッケージ ID から禁止トークンを導く（<b>全ドット接頭辞</b>）。
    /// <c>Npgsql.EntityFrameworkCore.PostgreSQL</c> →
    /// <c>Npgsql</c> / <c>Npgsql.EntityFrameworkCore</c> / <c>Npgsql.EntityFrameworkCore.PostgreSQL</c>。
    /// </summary>
    public static IEnumerable<string> TokensFromPackageId(string packageId)
    {
        var segments = packageId.Split('.');
        for (var take = 1; take <= segments.Length; take++)
        {
            yield return string.Join('.', segments.Take(take));
        }
    }

    /// <summary>
    /// 検査 (c) の母集合その 2: <b>リポジトリが実際に import している外部名前空間の根</b>。
    /// <para>
    /// パッケージ ID と名前空間の根は一致しないことがある（実測: パッケージ <c>WolverineFx</c> の
    /// 名前空間は <c>Wolverine</c>）。ID だけから導くと完全修飾での迂回を素通りさせる。
    /// CamelCase 分割で補う案は <c>OpenTelemetry</c> → <c>Open</c>、<c>SSH.NET</c> → <c>S</c> のような
    /// <b>危険なほど短いトークン</b>を生み、正当な記述（<c>quote.Open.Value</c>）を誤検出するため採らない。
    /// </para>
    /// <b>これも走査由来であり、手書きの拒否リストではない。</b>
    /// </summary>
    public static IReadOnlyList<string> ExternalNamespaceRootsIn(IEnumerable<string> sourceTexts)
    {
        var roots = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var text in sourceTexts)
        {
            foreach (var ns in UsingNamespacesIn(text))
            {
                var root = ns.Split('.')[0];
                if (root == "System" || root == UnitNamespace) continue;
                roots.Add(root);
            }
        }

        return roots.ToArray();
    }

    /// <summary>
    /// 検査 (c): トークンが<b>修飾名の先頭として</b>ソースに現れるか。
    /// <para>
    /// 直前が <c>.</c> または識別子文字である場合は一致とみなさない。名前空間の根が
    /// 修飾名の途中に現れることはなく、この制約が <c>quote.Open.Value</c> のような
    /// 正当なメンバアクセスの誤検出を防ぐ。直後は <c>.</c> を要求する（型・名前空間の修飾であること）。
    /// </para>
    /// </summary>
    public static bool ContainsQualifiedNameRoot(string sourceText, string token) =>
        Regex.IsMatch(sourceText, @"(?<![A-Za-z0-9_.])" + Regex.Escape(token) + @"\.");

    /// <summary>ソース中に現れた禁止トークン（検査 (c) の違反）。</summary>
    public static IReadOnlyList<string> ForbiddenLibraryTokensIn(string sourceText, IEnumerable<string> tokens) =>
        tokens.Where(t => ContainsQualifiedNameRoot(sourceText, t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();
}
