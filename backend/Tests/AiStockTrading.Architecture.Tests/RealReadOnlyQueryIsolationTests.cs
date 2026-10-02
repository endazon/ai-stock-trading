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
/// ③実弾の取引環境（<c>TrdEnv_Real</c>）を書いてよいのは照会側だけであること、の 3 つを見る。
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
        // 発注・訂正・取消・解錠の SDK メソッド（MMAPI_Trd のメソッド名）
        "PlaceOrder", "ModifyOrder", "PlaceComboOrder", "UnlockTrade",
    ];

    /// <summary>発注・訂正・解錠の<b>要求</b>の型（コールバックの <c>.Response</c> は SDK の面が要求するため許す）。</summary>
    private static readonly Regex OrderRequestType = new(
        @"\bTrd(?:PlaceOrder|ModifyOrder|PlaceComboOrder|UnlockTrade)\s*\.\s*(?:Request|C2S)\b", RegexOptions.Compiled);

    private static readonly Regex RealTradingEnv = new(@"\bTrdEnv_Real\b", RegexOptions.Compiled);

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
}
