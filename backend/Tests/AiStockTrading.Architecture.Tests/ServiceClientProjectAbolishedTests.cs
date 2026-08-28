using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// NFR, #526, IADR-0264 決定 1:
/// <b>サービスが「他サービスへ公開するクライアントライブラリ」（<c>*.Client</c>）を持たない</b>ことを固定する。
/// <para>
/// 計画（platform の技術スタック標準）は「サービス公開クライアント（<c>*.Client</c>）は標準に加えない。
/// <b>キャッシュ・タイムアウト・リトライ・fail-safe・DI 拡張は呼び出し元サービスの
/// <c>Infrastructure</c> に置く</b>」と定めている —— 適切な値は<b>呼び出し元の要求で決まる</b>ためであり、
/// 呼び出し先が固定すると合わない側が回避策を書くことになる。
/// </para>
/// <para>
/// 🔴 <b>この検査は「複製を禁じる」ものではない。</b> 呼び出し元ごとに実装が複製されるのは計画が
/// 承知のうえで選んだ形である（それぞれ別々の値を持てることが目的）。止めたいのは
/// <b>「1 箇所へ集約した共有クライアントプロジェクト」の再出現</b>だけである。
/// </para>
/// </summary>
public class ServiceClientProjectAbolishedTests
{
    // 対（肯定形・先に置く）: 走査そのものが空振りしていないこと。
    // サービスの csproj が 0 件になると、以下の検査は「違反なし」で無条件に緑になる。
    [Fact]
    public void サービスのプロジェクト探索が空振りしていない()
    {
        RepositoryLayout.ServiceProjectFiles.Should().HaveCountGreaterThan(
            10,
            "backend/Services 配下には 11 サービスぶんの csproj がある。"
                + "これを下回るなら探索が壊れている。実際に見つかったのは: {0}",
            string.Join(", ", RepositoryLayout.ServiceProjectFiles.Select(Path.GetFileNameWithoutExtension)));
    }

    [Fact]
    public void サービスは公開クライアントライブラリを持たない()
    {
        var violations = RepositoryLayout.ServiceProjectFiles
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(IsServiceClientProject)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        violations.Should().BeEmpty(
            "サービス公開クライアント（*.Client）は標準に加えない（#526 / IADR-0264 決定 1）。"
                + "キャッシュ・タイムアウト・リトライ・fail-safe・DI 拡張は"
                + "**呼び出し元サービスの Infrastructure/ExternalServices/** へ置くこと。違反: {0}",
            string.Join(" / ", violations));
    }

    // 否定形: 判定そのものが load-bearing であること。
    // 実ツリーの違反は 0 件であるため、判定が常に false を返すよう壊れても上の検査は緑のままになる。
    [Theory]
    [InlineData("ConfigurationService.Client", true)]
    [InlineData("SomeService.Client", true)]
    // テストプロジェクトも同じ理由で復活させない（実装が戻れば必ず伴う）。
    [InlineData("ConfigurationService.Client.Tests", true)]
    // 巻き添えにしてはならないもの: 名前の一部に Client を含むだけの型／プロジェクト。
    [InlineData("ConfigurationService", false)]
    [InlineData("TradeDecisionService.Infrastructure", false)]
    [InlineData("SomeService.ClientPortfolio", false)]
    public void 判定は公開クライアントプロジェクトだけを違反とする(string projectName, bool expected)
    {
        IsServiceClientProject(projectName).Should().Be(expected);
    }

    /// <summary>プロジェクト名が <c>*.Client</c>（またはその Tests）であるか。</summary>
    private static bool IsServiceClientProject(string projectName) =>
        projectName.EndsWith(".Client", StringComparison.Ordinal)
        || projectName.EndsWith(".Client.Tests", StringComparison.Ordinal);
}
