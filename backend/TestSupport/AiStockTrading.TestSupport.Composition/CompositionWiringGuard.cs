using System.Collections;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.CodeGeneration;
using Wolverine.Runtime.Handlers;

namespace AiStockTrading.TestSupport.Composition;

/// <summary>
/// NFR, #947, IADR-0397, IADR-0163 決定2: <b>本番の組み立て（Program.cs）を組んだホスト</b>を検査し、
/// 「配線が消えても全テストが緑のまま」の形を機械的に所見として返す。
/// <para>
/// 今週の監査が同じ形を 4 回実測した（PR #919 / #929 / #918 / #940）。いずれも、個々の試験は部品を
/// <b>自分で組む</b>（テスト内で new する・自前の DI を組む・偽物を差す）ため、本番の組み立てから依存が抜けても
/// 1 件も赤くならなかった。本エンジンは部品ではなく<b>本番の組み立てが実際に作った実体</b>を見る。
/// </para>
/// <list type="table">
/// <item><term>W0 組めない</term><description>本番型の登録・ハンドラを組み立てから作れない。<b>組めなかったものを「違反なし」に数えない</b>（unknown ≠ none）。</description></item>
/// <item><term>W1 省略可能依存の未解決</term><description>型で組む登録・ハンドラについて、DI が選ぶ構築子の既定値つき参照型引数を組み立てが解決できない（PR #919: 登録を消すと既定 NoOp へ黙って落ちる）。</description></item>
/// <item><term>W2 渡し忘れ</term><description>組み立てが作った実体の依存フィールドが null なのに組み立てはその型を解決できる、または本番の null-object を保持しているのに組み立ての解決先はそれではない（PR #918: ファクトリ登録が null を渡す）。</description></item>
/// <item><term>W3 偽物の陰の本物</term><description>組み立てがポートを本番の実装へ結線し、テストアセンブリがそのポートの偽物を持つのに、本物を 1 度も参照しない（PR #929 の前提: 本物を解決・参照する試験が無い）。</description></item>
/// </list>
/// <para>
/// 🔴 <b>本エンジンが見ないもの</b>（IADR-0397「残る制約」）: 本物の<b>中身の変異</b>（W3 は参照の有無だけを見る）／
/// 名前が null-object の規約（<c>NoOp*</c> / <c>Null*</c>）に従わない内部の代替／ファクトリ登録の構築子引数の省略で
/// 値型・文字列・デリゲートが既定値へ落ちること／サービス間 DTO の契約（#940 / #943 の形）。
/// </para>
/// </summary>
public static class CompositionWiringGuard
{
    public const string W0 = "W0";
    public const string W1 = "W1";
    public const string W2 = "W2";
    public const string W3 = "W3";

    private const int MaxDepth = 12;
    private const int MaxEnumeratedElements = 256;

    /// <summary>
    /// 組み立てを検査する。<paramref name="services"/> は本番の Program.cs が組んだホストのルート、
    /// <paramref name="descriptors"/> はその登録の写し（<see cref="CompositionGuardHost.InspectComposition{TEntryPoint}"/>）である。
    /// </summary>
    public static CompositionReport Inspect(
        IServiceProvider services,
        IEnumerable<ServiceDescriptor> descriptors,
        Assembly productionAssembly,
        Assembly testAssembly,
        IReadOnlyList<ServiceDescriptor>? deferredHostedServices = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(productionAssembly);
        ArgumentNullException.ThrowIfNull(testAssembly);

        var inspector = new Inspector(
            services, descriptors.ToList(), productionAssembly, testAssembly, deferredHostedServices ?? []);
        return inspector.Run();
    }

    /// <summary>本番型か。サービス自身のアセンブリと共有物（<c>AiStockTrading.Shared.*</c>）。</summary>
    internal static bool IsProduction(Type type, Assembly productionAssembly) =>
        type.Assembly == productionAssembly
        || (type.Assembly.GetName().Name?.StartsWith("AiStockTrading.Shared.", StringComparison.Ordinal) ?? false);

    /// <summary>本番の null-object か（命名規約 <c>NoOp*</c> / <c>Noop*</c> / <c>Null*</c>）。</summary>
    internal static bool IsNullObject(Type type) =>
        type.Name.StartsWith("NoOp", StringComparison.Ordinal)
        || type.Name.StartsWith("Noop", StringComparison.Ordinal)
        || type.Name.StartsWith("Null", StringComparison.Ordinal);

    /// <summary>所見の鍵に使う表示名（総称は <c>Name`1</c> のまま・入れ子は <c>+</c>）。</summary>
    internal static string DisplayName(Type type) => type.FullName ?? type.Name;

    /// <summary>主構築子の捕獲フィールド（<c>&lt;x&gt;P</c>）は引数名で表示する。</summary>
    internal static string FieldDisplayName(FieldInfo field)
    {
        var name = field.Name;
        if (name.Length > 3 && name[0] == '<' && name.EndsWith(">P", StringComparison.Ordinal))
            return name[1..^2];
        return name;
    }

    private sealed class Inspector(
        IServiceProvider root,
        IReadOnlyList<ServiceDescriptor> descriptors,
        Assembly production,
        Assembly tests,
        IReadOnlyList<ServiceDescriptor> deferredHosted)
    {
        private readonly List<CompositionFinding> _findings = [];
        private readonly HashSet<object> _visited = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<string> _findingKeys = new(StringComparer.Ordinal);
        private IServiceProviderIsService? _isService;
        private IServiceProvider _scope = default!; // Run() がスコープを入れる
        private int _roots;
        private int _handlers;
        private int _fields;
        private int _optionalParameters;
        private int _portsWithFakes;

        public CompositionReport Run()
        {
            using var scope = root.CreateScope();
            _scope = scope.ServiceProvider;
            _isService = root.GetService<IServiceProviderIsService>();

            InspectRegistrations();
            InspectHandlers();
            InspectFakeShadowedPorts();

            return new CompositionReport(
                _findings.OrderBy(f => f.Key, StringComparer.Ordinal).ToList(),
                _roots, _handlers, _fields, _optionalParameters, _portsWithFakes);
        }

        private bool IsProd(Type type) => IsProduction(type, production);

        private bool IsService(Type type)
        {
            if (_isService is null) return false;
            try { return _isService.IsService(type); }
            catch (Exception) { return false; }
        }

        private object? TryResolve(Type type)
        {
            try { return _scope.GetService(type); }
            catch (Exception) { return null; }
        }

        private void Add(string rule, string key, string detail)
        {
            var full = $"{rule} {key}";
            if (_findingKeys.Add(full))
                _findings.Add(new CompositionFinding(rule, full, detail));
        }

        // ---- 登録（本番型・常駐）を組み立てから解決し、W1 と W2 を見る ----
        private void InspectRegistrations()
        {
            var groups = descriptors
                .Where(d => !d.ServiceType.IsGenericTypeDefinition)
                .Where(Qualifies)
                .GroupBy(d => (d.ServiceType, Key: d.IsKeyedService ? d.ServiceKey : null))
                .ToList();

            foreach (var group in groups)
            {
                foreach (var d in group)
                {
                    var implementation = d.IsKeyedService ? d.KeyedImplementationType : d.ImplementationType;
                    if (implementation is not null && IsProd(implementation) && !implementation.IsGenericTypeDefinition)
                        CheckOptionalParameters(implementation, "登録");
                }

                IEnumerable<object?> instances;
                try
                {
                    instances = group.Key.Key is null
                        ? _scope.GetServices(group.Key.ServiceType).ToList()
                        : ResolveKeyed(group.Key.ServiceType, group.Key.Key).ToList();
                }
                catch (Exception ex)
                {
                    Add(W0, DisplayName(group.Key.ServiceType),
                        $"本番の組み立てから解決できない（{ex.GetType().Name}: {FirstLine(ex.Message)}）");
                    continue;
                }

                foreach (var instance in instances)
                {
                    if (instance is null || !IsProd(instance.GetType())) continue;
                    _roots++;
                    Walk(instance, 0);
                }
            }

            // 自前の常駐は起動させずに（外界を巡回させずに）、登録どおりに組み立てのコンテナから作る。
            foreach (var d in deferredHosted)
            {
                object? instance;
                try
                {
                    instance = d.ImplementationType is { } type
                        ? ActivatorUtilities.CreateInstance(_scope, type)
                        : d.ImplementationFactory is { } factory
                            ? factory(_scope)
                            : d.ImplementationInstance;
                }
                catch (Exception ex)
                {
                    Add(W0, DescribeHosted(d),
                        $"常駐を本番の組み立てから構築できない（{ex.GetType().Name}: {FirstLine(ex.Message)}）");
                    continue;
                }

                if (instance is null || !IsProd(instance.GetType())) continue;
                _roots++;
                Walk(instance, 0);
            }
        }

        private static string DescribeHosted(ServiceDescriptor d) =>
            d.ImplementationType is { } t ? DisplayName(t)
            : d.ImplementationFactory is { } f ? $"IHostedService(factory {f.Method.DeclaringType?.FullName}.{f.Method.Name})"
            : "IHostedService(instance)";

        private IEnumerable<object?> ResolveKeyed(Type serviceType, object key)
        {
            if (_scope is not IKeyedServiceProvider keyed) return [];
            var enumerable = typeof(IEnumerable<>).MakeGenericType(serviceType);
            return keyed.GetKeyedService(enumerable, key) is IEnumerable all ? all.Cast<object?>() : [];
        }

        private bool Qualifies(ServiceDescriptor d)
        {
            if (d.ServiceType == typeof(IHostedService)) return true;
            if (IsProd(d.ServiceType)) return true;
            var implementation = d.IsKeyedService ? d.KeyedImplementationType : d.ImplementationType;
            return implementation is not null && IsProd(implementation);
        }

        // ---- Wolverine のハンドラ（DI に登録されない。Wolverine が組み立てのコンテナから構築する） ----
        private void InspectHandlers()
        {
            // HandlerGraph は ICodeFileCollection として登録される（WolverinePreGeneratedCodeAssertion と同じ入口）。
            var graphs = root.GetServices<ICodeFileCollection>().OfType<HandlerGraph>().ToList();
            if (graphs.Count == 0) return;

            var handlerTypes = graphs.SelectMany(g => g.AllChains())
                .SelectMany(chain => chain.Handlers)
                .Select(call => call.HandlerType)
                .Where(t => t is not null && IsProd(t) && !(t.IsAbstract && t.IsSealed))
                .Distinct()
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .ToList();

            foreach (var type in handlerTypes)
            {
                _handlers++;
                CheckOptionalParameters(type, "ハンドラ");
                object instance;
                try
                {
                    instance = ActivatorUtilities.CreateInstance(_scope, type);
                }
                catch (Exception ex)
                {
                    Add(W0, DisplayName(type),
                        $"ハンドラを本番の組み立てから構築できない（{ex.GetType().Name}: {FirstLine(ex.Message)}）");
                    continue;
                }

                Walk(instance, 0);
            }
        }

        // ---- W1: DI が選ぶ構築子の、既定値つき参照型引数が組み立てで解決できない ----
        private void CheckOptionalParameters(Type type, string origin)
        {
            var ctor = SelectConstructor(type);
            if (ctor is null) return;

            foreach (var p in ctor.GetParameters())
            {
                if (!p.HasDefaultValue || !IsDependencyType(p.ParameterType)) continue;
                _optionalParameters++;
                if (IsService(p.ParameterType)) continue;
                Add(W1, $"{DisplayName(type)}({p.Name})",
                    $"{origin}の構築子引数 {p.Name}（{DisplayName(p.ParameterType)}）は省略可能で、本番の組み立てが解決できない。"
                    + "DI は既定値（null や内部の NoOp）で黙って組む（PR #919 の形）");
            }
        }

        // Microsoft.Extensions.DependencyInjection と同じ選び方: 満たせる引数が最も多い公開構築子
        // （既定値つき引数は満たせる側に数える）。
        private ConstructorInfo? SelectConstructor(Type type) =>
            type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault(c => c.GetParameters().All(p => p.HasDefaultValue || IsService(p.ParameterType)));

        private static bool IsDependencyType(Type type) =>
            !type.IsValueType
            && type != typeof(string)
            && !typeof(Delegate).IsAssignableFrom(type);

        // ---- W2: 組み立てが作った実体を本番型の範囲で辿る ----
        private void Walk(object instance, int depth)
        {
            if (depth > MaxDepth || !_visited.Add(instance)) return;

            var type = instance.GetType();
            if (!IsProd(type)) return;

            for (var t = type; t is not null && t != typeof(object) && IsProd(t); t = t.BaseType)
            {
                foreach (var field in t.GetFields(
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var fieldType = field.FieldType;
                    if (fieldType.IsValueType || fieldType == typeof(string) || typeof(Delegate).IsAssignableFrom(fieldType))
                        continue;

                    object? value;
                    try { value = field.GetValue(instance); }
                    catch (Exception) { continue; }

                    if (!IsDependencyField(field, t)) continue;
                    _fields++;

                    if (value is null)
                    {
                        if (IsService(fieldType) && TryResolve(fieldType) is { } available)
                        {
                            Add(W2, $"{DisplayName(t)}.{FieldDisplayName(field)}",
                                $"依存 {FieldDisplayName(field)}（{DisplayName(fieldType)}）が null だが、本番の組み立ては "
                                + $"{DisplayName(available.GetType())} を解決できる（組み立てが渡し忘れている。PR #918 の形）");
                        }

                        continue;
                    }

                    var valueType = value.GetType();
                    if (IsProd(valueType) && IsNullObject(valueType))
                    {
                        var resolved = IsService(fieldType) ? TryResolve(fieldType) : null;
                        // 装飾（t 自身がこのポートの解決先）の内側は、組み立てが包むと決めた実体である。比べる相手が無い。
                        if (resolved?.GetType() != t && resolved?.GetType() != valueType)
                        {
                            Add(W2, $"{DisplayName(t)}.{FieldDisplayName(field)}",
                                $"依存 {FieldDisplayName(field)}（{DisplayName(fieldType)}）が null-object {valueType.Name} を保持するが、"
                                + (resolved is null
                                    ? "本番の組み立てはこの型を登録していない（内部の既定値へ黙って落ちている。PR #919 の形）"
                                    : $"本番の組み立ての解決先は {DisplayName(resolved.GetType())} である（組み立てが渡し忘れている）"));
                        }
                    }

                    WalkValue(value, depth);
                }
            }
        }

        private void WalkValue(object value, int depth)
        {
            if (IsProd(value.GetType()))
            {
                Walk(value, depth + 1);
                return;
            }

            if (value is IEnumerable sequence and not string && value is not IDictionary)
            {
                var n = 0;
                try
                {
                    foreach (var element in sequence)
                    {
                        if (++n > MaxEnumeratedElements) break;
                        if (element is not null && IsProd(element.GetType())) Walk(element, depth + 1);
                    }
                }
                catch (Exception)
                {
                    // 列挙できない列（遅延評価で外界を読む等）は辿らない。
                }
            }
        }

        // 依存フィールド: 宣言型の構築子が同じ型の引数を持つフィールド（主構築子の捕獲 <x>P を含む）。
        // 状態のフィールド（キャッシュ・直近値）を依存と取り違えないための絞り込み。
        private static bool IsDependencyField(FieldInfo field, Type declaringType) =>
            declaringType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(c => c.GetParameters())
                .Any(p => p.ParameterType == field.FieldType);

        // ---- W3: 偽物の陰で本物が 1 度も試験に参照されないポート ----
        private void InspectFakeShadowedPorts()
        {
            var referenced = ReferencedTypeNames(tests);
            var testTypes = LoadableTypes(tests);

            var ports = descriptors
                .Where(d => !d.IsKeyedService && d.ServiceType.IsInterface && !d.ServiceType.IsGenericType)
                .Select(d => d.ServiceType)
                .Where(IsProd)
                .Distinct()
                .OrderBy(t => t.FullName, StringComparer.Ordinal);

            foreach (var port in ports)
            {
                var fakes = testTypes.Where(t => t.IsClass && !t.IsAbstract && port.IsAssignableFrom(t)).ToList();
                if (fakes.Count == 0) continue;

                var real = TryResolve(port);
                if (real is null) continue; // W0 / 未登録は他の規則が見る
                var realType = real.GetType();
                if (!IsProd(realType) || realType.Assembly == tests) continue;
                _portsWithFakes++;

                if (referenced.Contains(DisplayName(realType))) continue;
                // 共有物の本物は、その共有物自身の試験（backend/Shared/<アセンブリ名>.Tests）が持つ。
                if (realType.Assembly != production && SharedTestsMention(realType)) continue;

                Add(W3, $"{DisplayName(port)} -> {DisplayName(realType)}",
                    $"試験は偽物（{string.Join(", ", fakes.Select(f => f.Name))}）を使い、本番の組み立てが結線する本物 "
                    + $"{realType.Name} をテストアセンブリが 1 度も参照しない（本物の中身が変わっても緑のまま。PR #929 の形）");
            }
        }

        private readonly Dictionary<Assembly, string?> _sharedTestSources = [];

        // 共有アセンブリの兄弟試験プロジェクトのソースが型名を識別子として含むか（行コメントは除く）。
        // メタデータでなくソースを読むのは、サービス単体の試験実行では兄弟の試験 DLL がビルドされていないためである。
        private bool SharedTestsMention(Type type)
        {
            if (!_sharedTestSources.TryGetValue(type.Assembly, out var text))
            {
                text = ReadSharedTestSources(type.Assembly);
                _sharedTestSources[type.Assembly] = text;
            }

            return text is not null
                && Regex.IsMatch(text, $@"\b{Regex.Escape(type.Name)}\b", RegexOptions.CultureInvariant);
        }

        private string? ReadSharedTestSources(Assembly shared)
        {
            var backend = FindBackendRoot(Path.GetDirectoryName(tests.Location));
            var name = shared.GetName().Name;
            if (backend is null || name is null) return null;
            var dir = Path.Combine(backend, "Shared", name + ".Tests");
            if (!Directory.Exists(dir)) return null;

            var sb = new System.Text.StringBuilder();
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var sep = Path.DirectorySeparatorChar;
                if (file.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
                    || file.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
                    continue;
                foreach (var line in File.ReadLines(file))
                {
                    var i = line.IndexOf("//", StringComparison.Ordinal);
                    sb.AppendLine(i < 0 ? line : line[..i]);
                }
            }

            return sb.ToString();
        }

        private static string? FindBackendRoot(string? start)
        {
            for (var dir = start; dir is not null; dir = Path.GetDirectoryName(dir))
            {
                if (File.Exists(Path.Combine(dir, "backend.slnx"))) return dir;
            }

            return null;
        }

        private static IReadOnlyList<Type> LoadableTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null).ToList()!; }
        }

        private static string FirstLine(string message)
        {
            var i = message.IndexOfAny(['\r', '\n']);
            return i < 0 ? message : message[..i];
        }
    }

    /// <summary>
    /// テストアセンブリが参照する外部型の完全名（メタデータの TypeRef 表）。<c>typeof</c>・<c>new</c>・総称引数・
    /// メンバ参照のいずれでも TypeRef が 1 行できる。<c>nameof</c> は文字列へ畳まれるため数えない（参照ではない）。
    /// </summary>
    public static IReadOnlySet<string> ReferencedTypeNames(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in reader.TypeReferences)
            names.Add(TypeReferenceName(reader, handle));
        return names;
    }

    private static string TypeReferenceName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var tr = reader.GetTypeReference(handle);
        var name = reader.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return TypeReferenceName(reader, (TypeReferenceHandle)tr.ResolutionScope) + "+" + name;
        var ns = reader.GetString(tr.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }
}

/// <summary>検査の所見。<see cref="Key"/> は allowlist の鍵（規則＋型・引数・フィールド・ポート）。</summary>
public sealed record CompositionFinding(string Rule, string Key, string Detail)
{
    public override string ToString() => $"{Key}\n      {Detail}";
}

/// <summary>検査の結果と、母集団が痩せていないことを表明するための件数。</summary>
public sealed record CompositionReport(
    IReadOnlyList<CompositionFinding> Findings,
    int RootsComposed,
    int HandlersComposed,
    int FieldsInspected,
    int OptionalParametersInspected,
    int PortsWithFakes);
