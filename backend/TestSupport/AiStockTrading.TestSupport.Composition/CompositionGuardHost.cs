using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiStockTrading.TestSupport.Composition;

/// <summary>
/// NFR, #947, IADR-0397: 各サービスの既存ファクトリ（<c>WebApplicationFactory&lt;Program&gt;</c>）から本番の組み立てを組み、
/// <see cref="CompositionWiringGuard"/> へ渡す入口と、allowlist つきの判定。
/// </summary>
public static partial class CompositionGuardHost
{
    /// <summary>
    /// ファクトリが組むホスト（本番の Program.cs ＋ 伝送の境界だけの差し替え）を検査する。
    /// <para>
    /// 登録の写しは、ファクトリ自身の差し替えより<b>後</b>に走る ConfigureServices で取る
    /// （minimal hosting では Program.cs の登録がすべて済んだ後に呼ばれる）。
    /// <b>自前の常駐（本番アセンブリの IHostedService）は起動させない</b> —— 外界を巡回させず、検査の結果を
    /// 巡回のタイミングから独立させるためである。代わりに写しの登録どおりに組み立てのコンテナから作って辿る。
    /// </para>
    /// </summary>
    public static CompositionReport InspectComposition<TEntryPoint>(
        this WebApplicationFactory<TEntryPoint> factory,
        Assembly testAssembly,
        Action<IWebHostBuilder>? configure = null)
        where TEntryPoint : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        var production = typeof(TEntryPoint).Assembly;
        List<ServiceDescriptor>? snapshot = null;
        List<ServiceDescriptor> ownHosted = [];
        using var derived = factory.WithWebHostBuilder(builder =>
        {
            configure?.Invoke(builder);
            builder.ConfigureServices(services =>
            {
                snapshot = [.. services];
                ownHosted = services.Where(d => IsOwnHostedService(d, production)).ToList();
                foreach (var d in ownHosted) services.Remove(d);
            });
        });

        var services = derived.Services; // ホストを組んで起動する（自前の常駐は除いてある）
        if (snapshot is null)
            throw new InvalidOperationException("登録の写しを取れなかった（ConfigureServices が呼ばれていない）。検査を空で通さない");

        return CompositionWiringGuard.Inspect(services, snapshot, production, testAssembly, ownHosted);
    }

    private static bool IsOwnHostedService(ServiceDescriptor d, Assembly production)
    {
        if (d.ServiceType != typeof(IHostedService) || d.IsKeyedService) return false;
        var origin = d.ImplementationType
                     ?? d.ImplementationFactory?.Method.DeclaringType
                     ?? d.ImplementationInstance?.GetType();
        return origin is not null && CompositionWiringGuard.IsProduction(origin, production);
    }

    /// <summary>
    /// 所見が allowlist と過不足なく一致することを表明する。<b>allowlist はラチェット</b>である ——
    /// 所見が消えた行（外し忘れ）も赤にする。各行の理由は issue 番号（<c>#NNN</c>）を含まなければならない。
    /// </summary>
    public static void AssertNoUnexpectedFindings(
        this CompositionReport report, IReadOnlyDictionary<string, string> allowlist)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(allowlist);

        var keys = report.Findings.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var unexpected = report.Findings.Where(f => !allowlist.ContainsKey(f.Key)).ToList();
        var stale = allowlist.Keys.Where(k => !keys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var unjustified = allowlist.Where(kv => !IssueRef().IsMatch(kv.Value)).Select(kv => kv.Key).ToList();

        if (unexpected.Count == 0 && stale.Count == 0 && unjustified.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine("本番の組み立てに配線の抜けがある（IADR-0397。直し方は同 IADR「所見への対処」）。");
        if (unexpected.Count > 0)
        {
            sb.AppendLine($"■ 所見 {unexpected.Count} 件:");
            foreach (var f in unexpected) sb.AppendLine($"  - {f}");
        }

        if (stale.Count > 0)
        {
            sb.AppendLine($"■ 実体を失った allowlist 行 {stale.Count} 件（外す）:");
            foreach (var k in stale) sb.AppendLine($"  - {k}");
        }

        if (unjustified.Count > 0)
        {
            sb.AppendLine($"■ 理由に issue 番号（#NNN）の無い allowlist 行 {unjustified.Count} 件:");
            foreach (var k in unjustified) sb.AppendLine($"  - {k}");
        }

        throw new CompositionGuardException(sb.ToString());
    }

    /// <summary>
    /// 母集団が痩せていないことを表明する。0 件になると上の判定は「所見なし」で無条件に緑になる
    /// （unknown ≠ none。下限は実測の半分程度に置き、実測値は各ガードのコメントに残す）。
    /// </summary>
    public static void AssertPopulation(
        this CompositionReport report, int minRoots, int minFields, int minHandlers = 0)
    {
        ArgumentNullException.ThrowIfNull(report);
        var problems = new List<string>();
        if (report.RootsComposed < minRoots)
            problems.Add($"組み立てから作った本番型の根が {report.RootsComposed} 件（下限 {minRoots}）");
        if (report.FieldsInspected < minFields)
            problems.Add($"辿った依存フィールドが {report.FieldsInspected} 件（下限 {minFields}）");
        if (report.HandlersComposed < minHandlers)
            problems.Add($"構築した Wolverine ハンドラが {report.HandlersComposed} 件（下限 {minHandlers}）");
        if (problems.Count > 0)
            throw new CompositionGuardException(
                "組み立てガードの母集団が痩せている（検査が空振りしている）: " + string.Join(" / ", problems));
    }

    /// <summary>件数と所見の一覧（試験出力へ残す）。</summary>
    public static string Describe(this CompositionReport report, string label)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.AppendLine(
            $"[{label}] roots={report.RootsComposed} handlers={report.HandlersComposed} fields={report.FieldsInspected} "
            + $"optionalParameters={report.OptionalParametersInspected} fakedPorts={report.PortsWithFakes} findings={report.Findings.Count}");
        foreach (var f in report.Findings) sb.AppendLine($"  - {f}");
        return sb.ToString();
    }

    [GeneratedRegex(@"#\d+")]
    private static partial Regex IssueRef();
}

/// <summary>組み立てガードの失敗。</summary>
public sealed class CompositionGuardException(string message) : Exception(message);
