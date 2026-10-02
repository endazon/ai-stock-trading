using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// T-10-2063, FR-10, UC-06, ADR-0016 決定3（2026-08-06 追記）, #1000, IADR-0482 決定2:
/// <b>実弾口座の読み取り専用の照会（発注執行の <c>Infrastructure/ExternalServices/RealReadOnly/</c>）から、
/// 発注の型・発注 API へ構造的に到達できない</b>ことをソース走査で固定する（IADR-0256 と同じ作法・被検査コードを参照しない）。
/// <para>
/// 発注執行は単一プロジェクト（IADR-0259）であり、プロジェクト参照の向きでは切り離せない。そこで
/// ①照会側のソースに発注の型・発注メソッド・発注要求の型が<b>識別子として現れない</b>こと、
/// ②照会側の型を参照するのは合成起点（<c>Program.cs</c>）だけで、発注の機能（Features・Steps・Hosted・発注のアダプタ）が参照しないこと、
/// ③実弾の取引環境（<c>TrdEnv_Real</c>）を書いてよいのは照会側だけであること、
/// ④照会側の外が照会側の内部（実弾ヘッダの組み立て）へ文字列・リフレクションを介しても到達しないこと（独立監査 🟡3・2026-10-02）、の 4 つを見る。
/// 型の上の検査（実装するインターフェース・シームの面）は発注執行の試験（<c>RealReadOnlyTypeIsolationTests</c>）が持つ。
/// </para>
/// </summary>
public class RealReadOnlyQueryIsolationTests
{
    private static readonly string ServiceRoot =
        Path.Combine(RepositoryLayout.Root, "backend", "Services", "OrderExecutionService");

    private static readonly string RealReadOnlyDir =
        Path.Combine(ServiceRoot, "Infrastructure", "ExternalServices", "RealReadOnly");

    /// <summary>照会側に現れてはならない識別子（発注の型・発注の SDK メソッド・発注クライアントの実体）。</summary>
    private static readonly string[] ForbiddenIdentifiers =
    [
        // 発注の面を持つ型（SIMULATE の発注経路）
        "IMoomooTradeClient", "MMApiMoomooTradeClient", "IMoomooTradeConnection", "IMoomooTradeConnectionFactory",
        "MMApiTradeConnectionFactory", "IBrokerAdapter", "MoomooBrokerAdapter", "BrokerFactory",
        "IOrderAmendmentBroker", "IClientOrderIdBroker", "OrderExecutionAppService", "OrderAmendmentService",
        // 発注側のポート（発注クライアント・発注のアダプタ・予約の照会が実装するもの。独立監査 🟡3・2026-10-02 で母集合を引き直した:
        // 発注執行の interface 宣言と、MMApiMoomooTradeClient / MoomooBrokerAdapter / MoomooReservationBrokerProbe が実装する
        // インターフェースの全数から、照会側自身のもの〔IShortPermitSource・IMoomooMarginQueryConnection(Factory)・
        // IRealReadOnlyQueryAudit〕・時計〔IClock〕・相場系〔IKLineQuotaQuery・IDailyKLineSource・IMoomooQotProbeConnection(Factory)〕を除いた残り）
        "IOrderFeeQuery", "IProbeOutputRedactor", "IBrokerAccountSource", "IBrokerPositionSource", "IBrokerAvailabilityProbe",
        "IProtectiveOrderBroker", "IAlternativeProtectiveOrderBroker", "IClassifiedPositionSource",
        "IReservationBrokerProbe", "MoomooReservationBrokerProbe", "IndeterminateReservationBrokerProbe",
        "IOrderExpenseSource", "IExecutedOrderStore", "IOrderLifecycleStore", "IOrderReservationStore", "IProtectiveStopOrderStore",
        "IReservationReconciliationSink", "IReconciledEntryProtection",
        // 発注・訂正・取消・解錠の SDK メソッド（MMAPI_Trd のメソッド名）
        "PlaceOrder", "ModifyOrder", "PlaceComboOrder", "UnlockTrade",
    ];

    /// <summary>発注・訂正・解錠の<b>要求</b>の型（コールバックの <c>.Response</c> は SDK の面が要求するため許す）。</summary>
    private static readonly Regex OrderRequestType = new(
        @"\bTrd(?:PlaceOrder|ModifyOrder|PlaceComboOrder|UnlockTrade)\s*\.\s*(?:Request|C2S)\b", RegexOptions.Compiled);

    private static readonly Regex RealTradingEnv = new(@"\bTrdEnv_Real\b", RegexOptions.Compiled);

    /// <summary>照会側の内部（実弾ヘッダの組み立て）を指す名前。照会側の外では識別子にも文字列にも現れてはならない。</summary>
    private static readonly string[] RealReadOnlyInternalNames = ["BuildRealHeader", "ExternalServices.RealReadOnly"];

    /// <summary>
    /// 名前で列挙値を引く（<c>Enum.Parse</c> 等）ための文字列。<b>文字列全体がこの名前のときだけ</b>止める
    /// （閂の拒否文言「実弾（TrdEnv_Real）は未解禁です」のような散文の言及は利用ではない）。
    /// </summary>
    private static readonly Regex RealTradingEnvNameLiteral = new(@"^[$@]*""TrdEnv_Real""$", RegexOptions.Compiled);

    /// <summary>
    /// 名前（文字列）で型・メンバへ到達するリフレクションの API。発注執行の本番コードは現に 1 つも使っていない（2026-10-02 実測）。
    /// <c>GetType()</c>（引数なし）は実行時の型を取るだけなので許し、引数つき（<c>Type.GetType("…")</c>）だけを止める。
    /// </summary>
    private static readonly string[] ReflectionIdentifiers =
    [
        "GetMethod", "GetMethods", "GetRuntimeMethod", "GetRuntimeMethods", "GetField", "GetFields", "GetProperty", "GetProperties",
        "GetMember", "GetMembers", "InvokeMember", "CreateInstance", "CreateDelegate", "MethodInfo", "FieldInfo", "BindingFlags",
    ];

    private static readonly Regex GetTypeByName = new(@"\bGetType\s*\(\s*[^\s)]", RegexOptions.Compiled);

    private static IReadOnlyList<string> RealReadOnlySources() =>
        Directory.Exists(RealReadOnlyDir)
            ? Directory.EnumerateFiles(RealReadOnlyDir, "*.cs", SearchOption.AllDirectories)
                .Where(RepositoryLayout.NotUnderBuildOutput).OrderBy(p => p, StringComparer.Ordinal).ToArray()
            : [];

    private static IReadOnlyList<string> ServiceProductionSources() =>
        Directory.EnumerateFiles(ServiceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(RepositoryLayout.NotUnderBuildOutput)
            .Where(p => !p.Replace('\\', '/').Contains("/Tests/", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

    private static string Blanked(string path) => CSharpSource.BlankCommentsAndLiterals(File.ReadAllText(path));

    /// <summary>コメントだけを潰し、文字列リテラルは原文のまま残す（文字列で名前を渡す抜け道を読むため）。</summary>
    private static string CommentsBlanked(string path)
    {
        var source = File.ReadAllText(path);
        var buffer = CSharpSource.BlankCommentsAndLiterals(source).ToCharArray();
        foreach (var (start, text) in CSharpSource.StringLiterals(source))
            text.CopyTo(0, buffer, start, text.Length);
        return new string(buffer);
    }

    private static bool IsRealReadOnly(string path) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(RealReadOnlyDir) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    // 対（肯定形・先に置く）: 走査の対象が実在する。移動・改名で「0 件走査で緑」にならない。
    [Fact]
    public void 照会側のソースが実在し_実弾ヘッダを作るクライアントを含む()
    {
        var sources = RealReadOnlySources();
        sources.Select(Path.GetFileName).Should().Contain(
            ["MMApiRealMarginQueryClient.cs", "IMoomooMarginQueryConnection.cs", "RealMarginQueryOptions.cs"],
            "照会側（RealReadOnly/）が見つからない。移動したなら本検査の走査先を直すこと（黙って 0 件にしない）");
        sources.Should().Contain(p => RealTradingEnv.IsMatch(Blanked(p)), "照会側は実弾のヘッダを作る（作っていなければ走査先が違う）");
    }

    [Fact]
    public void 照会側のソースに発注の型や発注APIが現れない()
    {
        var violations = new List<string>();
        foreach (var path in RealReadOnlySources())
        {
            var blanked = Blanked(path);
            var identifiers = CSharpSource.Identifiers(blanked);
            violations.AddRange(ForbiddenIdentifiers.Where(identifiers.Contains)
                .Select(id => $"{Path.GetFileName(path)}: {id}"));
            violations.AddRange(OrderRequestType.Matches(blanked).Select(m => $"{Path.GetFileName(path)}: {m.Value}"));
        }

        violations.Should().BeEmpty(
            "実弾口座の読み取り専用の照会から発注経路へ到達できてはならない（#1000 の裁定・IADR-0482 決定2）");
    }

    [Fact]
    public void 照会側の型を参照するのは合成起点だけである()
    {
        var realReadOnlyTypes = RealReadOnlySources()
            .SelectMany(p => Regex.Matches(Blanked(p), @"\b(?:class|interface|record)\s+([A-Za-z_]\w*)").Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        realReadOnlyTypes.Should().Contain("MMApiRealMarginQueryClient");

        var referrers = ServiceProductionSources()
            .Where(p => !IsRealReadOnly(p))
            .Where(p => CSharpSource.Identifiers(Blanked(p)).Overlaps(realReadOnlyTypes)
                     || Blanked(p).Contains("ExternalServices.RealReadOnly", StringComparison.Ordinal))
            .Select(p => Path.GetRelativePath(ServiceRoot, p).Replace('\\', '/'))
            .ToArray();

        referrers.Should().BeEquivalentTo(["Program.cs"],
            "照会側は IShortPermitSource として合成起点で結線するだけで、発注の機能から直接参照させない（IADR-0482 決定2）");
    }

    [Fact]
    public void 実弾の取引環境を書いてよいのは照会側だけである()
    {
        var writers = ServiceProductionSources()
            .Where(p => RealTradingEnv.IsMatch(Blanked(p)))
            .Where(p => !IsRealReadOnly(p))
            .Select(p => Path.GetRelativePath(ServiceRoot, p).Replace('\\', '/'))
            .ToArray();

        writers.Should().BeEmpty(
            "発注経路のヘッダは SIMULATE 固定（閂 2）。実弾の取引環境は読み取り専用の照会のヘッダにだけ現れる（IADR-0482 決定2）");
    }

    // 独立監査 🟡3（#1000・2026-10-02）: 識別子の走査は<b>文字列で名前を渡す経路</b>（リテラル・リフレクション）を見ない。
    // 照会側の外（発注クライアント・発注のアダプタ・Features・Hosted・合成起点）で、
    // ①実弾ヘッダの組み立て（BuildRealHeader）・実弾の取引環境・照会側の名前空間を、識別子としても文字列リテラルとしても書かない、
    // ②照会側の型名を文字列リテラルに書かない（合成起点の補間ホールの中の型参照は、上の「参照するのは合成起点だけ」が見る）、
    // ③名前でメンバ・型へ到達するリフレクション API を使わない、の 3 つを見る。
    // 🔴 網羅ではない（dynamic・式木・DI の型走査・別アセンブリ経由は見えない）。IADR-0482 の盲点に「リフレクション」として残す。
    [Fact]
    public void 照会側の外は照会側の内部へ文字列やリフレクションでも到達しない()
    {
        var realReadOnlyTypes = RealReadOnlySources()
            .SelectMany(p => Regex.Matches(Blanked(p), @"\b(?:class|interface|record)\s+([A-Za-z_]\w*)").Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        realReadOnlyTypes.Should().Contain("MMApiRealMarginQueryClient");

        var outside = ServiceProductionSources().Where(p => !IsRealReadOnly(p)).ToArray();
        outside.Select(Path.GetFileName).Should().Contain(["MMApiMoomooTradeClient.cs", "MoomooBrokerAdapter.cs", "Program.cs"],
            "発注クライアント・発注のアダプタが走査の対象に入っていること（移動で 0 件走査にしない）");

        var violations = new List<string>();
        foreach (var path in outside)
        {
            var name = Path.GetRelativePath(ServiceRoot, path).Replace('\\', '/');
            var withLiterals = CommentsBlanked(path);
            var blanked = Blanked(path);
            var identifiers = CSharpSource.Identifiers(blanked);
            var literals = LiteralsOf(path).ToArray();
            violations.AddRange(RealReadOnlyInternalNames
                .Where(n => literals.Any(l => l.Contains(n, StringComparison.Ordinal)))
                .Select(n => $"{name}: \"{n}\"（文字列リテラル）"));
            violations.AddRange(literals.Where(l => RealTradingEnvNameLiteral.IsMatch(l))
                .Select(l => $"{name}: {l}（名前で実弾の取引環境を引く）"));
            if (identifiers.Contains("BuildRealHeader"))
                violations.Add($"{name}: BuildRealHeader（識別子・nameof を含む）");
            if (name != "Program.cs")
            {
                violations.AddRange(literals
                    .SelectMany(l => realReadOnlyTypes.Where(t => l.Contains(t, StringComparison.Ordinal)))
                    .Select(t => $"{name}: \"{t}\"（文字列リテラル）"));
            }
            violations.AddRange(ReflectionIdentifiers.Where(identifiers.Contains).Select(id => $"{name}: {id}（リフレクション）"));
            violations.AddRange(GetTypeByName.Matches(withLiterals).Select(m => $"{name}: {m.Value.Trim()}（名前で型を引く）"));
        }

        violations.Should().BeEmpty(
            "発注側から照会側の内部（実弾ヘッダ）へは、文字列やリフレクションを介しても到達できてはならない（IADR-0482 決定2）");
    }

    private static IEnumerable<string> LiteralsOf(string path) =>
        CSharpSource.StringLiterals(File.ReadAllText(path)).Select(l => l.Text);
}
