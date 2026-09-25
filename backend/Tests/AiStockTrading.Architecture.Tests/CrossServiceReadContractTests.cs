using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// NFR, #952, IADR-0420（T-10-945〜947）: <b>サービス間 HTTP の受け手の操作は、送り手の本物の型による契約テストを持つ</b>ことを表明する。
/// <para>
/// PR #940（<c>WorkingEntryOrderView</c>）と #943（<c>/open-positions</c>）で、送り手の DTO の項目名を変えても<b>両サービスの全テストが
/// 緑のまま</b>、実行時は受け手が既定値で読んで「保有なし」へ黙って倒れる形が実測された。個々の経路は #943・#957・#990 で契約テストを
/// 足したが、<b>新しい経路は契約テストなしで増え得る</b>。組み立てガード（IADR-0397）は 1 サービスの組み立てしか組まないため、この形を見ない。
/// </para>
/// <para>
/// 規約（IADR-0420 決定1・2）: 受け手のテストが送り手を extern alias で参照し、送り手の本物の型を送り手の JSON 設定で直列化した本文を
/// 受け手のアダプタの操作に読ませる。送り手は本物の Program.cs の本文と応答型の直列化を突き合わせるテストで JSON 設定を固定する。
/// 走査と判定の設計は <see cref="CrossServiceReadContractScan"/> にある。
/// </para>
/// </summary>
public class CrossServiceReadContractTests
{
    /// <summary>
    /// 規約を満たさない単位のうち<b>既知のもの</b>。🔴 <b>無視リストではなくラチェットである</b> —— 契約テストが入る・単位が消えると
    /// 「実体を失った行」で赤くなり、外し忘れが残らない。理由には issue 番号（<c>#NNN</c>）と外す条件を書く。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> KnownWithoutContractTest =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CostControlService/HttpAssumptionsClient.FetchAsync -> ConfigurationService /assumptions"] =
                "偽陽性: 受け手・送り手とも共有型 VersionedAssumptions（Shared.Kernel）で読み書きし、項目名の変更は両側に同時に効く"
                + "（#943 の走査表の 6 行で対象外とした）。外す条件: 受け手が自前の型で読むようになったら契約テストを足して外す。",
            ["TradeDecisionService/HttpAssumptionsClient.FetchAsync -> ConfigurationService /assumptions"] =
                "偽陽性: 同上（共有型 VersionedAssumptions。#943 の走査表の 6 行）。外す条件: 同上。",
        };

    /// <summary>
    /// 本リポジトリのルートに 1 つも一致しない <c>Http*</c> アダプタ（送り手が本リポジトリの外）。これもラチェットである ——
    /// ルートに一致するようになった（＝送り手が本リポジトリへ来た）行は赤くなり、契約の単位へ入る。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ProvidersOutsideRepository =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ReportService/HttpReportNarrativeDrafter"] =
                "LLM ゲートウェイ（基盤リポジトリ）の /complete を呼ぶ。送り手の型は本リポジトリに無い（#943 の走査の除外）。",
            ["TradeDecisionService/HttpLlmCompletionClient"] =
                "同上（LLM ゲートウェイ。#943 の走査の除外）。",
        };

    private static readonly ReadContractAnalysis Repository =
        CrossServiceReadContractScan.Analyze(CrossServiceReadContractScan.Repository());

    // 対（肯定形・先に置く）: 母集合が痩せていないこと。0 件なら下の検査は無条件に緑になる。
    [Fact]
    public void サービス間の読み取りの母集合の探索が空振りしていない()
    {
        Repository.Adapters.Should().HaveCountGreaterThanOrEqualTo(
            20, "ExternalServices/Http*.cs のアダプタは 2026-09-25 実測で 25 本。見つかったのは: {0}", string.Join(", ", Repository.Adapters));
        Repository.ProviderRoutes.Should().HaveCountGreaterThanOrEqualTo(
            50, "送り手の Map* から得たルートは 2026-09-25 実測で 62 本。見つかったのは {0} 本", Repository.ProviderRoutes.Count);
        Repository.Units.Should().HaveCountGreaterThanOrEqualTo(
            26, "受け手の単位（公開操作 × 他サービスのルート・本文を読むもの）は 2026-09-25 実測で 32。見つかったのは: {0}",
            string.Join(" / ", Repository.Units.Select(u => u.Key)));
        Repository.Units.Select(u => u.Provider).Distinct().Should().HaveCountGreaterThanOrEqualTo(
            5, "送り手は 2026-09-25 実測で 6 サービス（リスク管理・報告書・市場監視・費用統制・監査・構成）");
    }

    /// <summary>T-10-945: 本体。<b>受け手の操作は、送り手の本物の型を直列化して読ませる契約テストを持つ。</b></summary>
    [Fact]
    public void サービス間の読み取りは送り手の型による契約テストを持つ()
    {
        var violations = Repository.Unsatisfied
            .Where(u => !KnownWithoutContractTest.ContainsKey(u.Key))
            .Select(u => u.Key)
            .ToArray();

        violations.Should().BeEmpty(
            "サービス間 HTTP の受け手の操作は、受け手のテストの 1 つのメソッドが、送り手を extern alias で参照して送り手の本物の型を"
                + "送り手の JSON 設定で直列化した本文を読ませ、その操作を呼ばなければならない（IADR-0420 決定1。雛形は "
                + "RiskManagementReadContractTests・OperationReadContractTests）。無いと送り手の項目名の変更が両サービスの試験を緑のまま"
                + "通り、実行時は既定値で読まれる（#940 / #943）。契約テストを持たない操作: {0}",
            string.Join(" / ", violations));
    }

    /// <summary>送り手側: 受け手の契約テストが前提にする JSON 設定を、送り手の本物の Program.cs で固定している。</summary>
    [Fact]
    public void 契約テストが読む送り手はJSON設定を固定するテストを持つ()
    {
        Repository.ProvidersWithoutWireFormatTest.Should().BeEmpty(
            "受け手の契約テストは「送り手がその JSON 設定で出している」ことを前提にし、受け手の側からはそれが見えない。送り手のテストは "
                + "本物の Program.cs（CreateClient）の本文と応答型の直列化を JsonNode.DeepEquals で突き合わせなければならない"
                + "（IADR-0420 決定2。雛形は各送り手の ReadContractWireFormatTests）。持たない送り手: {0}",
            string.Join(", ", Repository.ProvidersWithoutWireFormatTest));
    }

    /// <summary>母集合の閉包: 他サービスのルートを呼ぶ本番コードは <c>ExternalServices/Http*.cs</c> の中だけにある。</summary>
    [Fact]
    public void 他サービスのルートを読むコードはアダプタの中だけにある()
    {
        Repository.RouteLiteralsOutsideAdapters.Should().BeEmpty(
            "他サービスのルートに一致する文字列が ExternalServices/Http*.cs の外にある。そこから他サービスを呼ぶなら、本検査の母集合から"
                + "漏れる（契約テストの有無を見ない）。アダプタへ移すこと（IADR-0420 決定3）。該当: {0}",
            string.Join(" / ", Repository.RouteLiteralsOutsideAdapters));
    }

    [Fact]
    public void 本リポジトリのルートを呼ばないアダプタは理由つきで載っている()
    {
        var undeclared = Repository.AdaptersWithoutRepositoryRoute.Where(a => !ProvidersOutsideRepository.ContainsKey(a)).ToArray();

        undeclared.Should().BeEmpty(
            "本リポジトリのどのサービスのルートにも一致しない Http* アダプタは、送り手が本リポジトリの外であることを理由つきで "
                + "ProvidersOutsideRepository に載せる（ルートを定数の連結などで組んでいて走査が読めない場合は、リテラルで書く）。未申告: {0}",
            string.Join(", ", undeclared));
    }

    /// <summary>T-10-946: ラチェット。既知の一覧に<b>実体を失った行</b>が無く、各行が issue 番号を持つ。</summary>
    [Fact]
    public void 既知の一覧に実体を失った行が無い()
    {
        var unsatisfied = Repository.Unsatisfied.Select(u => u.Key).ToHashSet(StringComparer.Ordinal);
        var stale = KnownWithoutContractTest.Keys.Where(k => !unsatisfied.Contains(k))
            .Concat(ProvidersOutsideRepository.Keys.Where(k => !Repository.AdaptersWithoutRepositoryRoute.Contains(k)))
            .ToArray();

        stale.Should().BeEmpty(
            "既知として許容している行が、実ツリーではもう当たらない（契約テストが入った・アダプタが消えた・名前が変わった・送り手が本リポジトリへ来た）。"
                + "**一覧から外すこと** —— 残すと次の同型の欠落を素通しする。実体を失った行: {0}",
            string.Join(" / ", stale));
    }

    [Fact]
    public void 既知の一覧の各行は理由に_issue_番号を持つ()
    {
        var withoutIssue = KnownWithoutContractTest.Concat(ProvidersOutsideRepository)
            .Where(e => !Regex.IsMatch(e.Value, @"#\d+"))
            .Select(e => e.Key)
            .ToArray();

        withoutIssue.Should().BeEmpty("暫定の除外は理由・外す条件・issue 番号と一緒に書く（条件を書かない除外は恒久化する）。該当: {0}",
            string.Join(" / ", withoutIssue));
    }

    // ── T-10-947: 判定の自己試験（合成したソース）。実ツリーは所見 0 件なので、判定が常に「満たす」へ壊れても本体は緑のままになる。

    [Fact]
    public void 判定は送り手の型を直列化して操作を呼ぶ契約テストを満たすと数える()
    {
        var analysis = CrossServiceReadContractScan.Analyze(Tree(ReceiverTests(
            ContractTest("GetPositionsAsync", "PositionView"), ContractTest("GetOrdersAsync", "OrderView"))));

        analysis.Units.Select(u => u.Key).Should().Equal(
            "Receiver/HttpPositionClient.GetOrdersAsync -> Sender /api/orders",
            "Receiver/HttpPositionClient.GetPositionsAsync -> Sender /api/positions");
        analysis.Unsatisfied.Should().BeEmpty();
        analysis.ProvidersWithoutWireFormatTest.Should().BeEmpty();
    }

    [Fact]
    public void 判定は同じファイルに別の操作の契約しか無い形を検出する()
    {
        // #943 の形: 同じテストファイルに /api/orders の契約（送り手の型の直列化）があり、/api/positions は手書きの JSON だけ。
        var tests = ReceiverTests(
            ContractTest("GetOrdersAsync", "OrderView"),
            """
            [Fact]
            public async Task 手書き() { var r = await new HttpPositionClient(Client("[]")).GetPositionsAsync(); }
            """);

        var analysis = CrossServiceReadContractScan.Analyze(Tree(tests));

        analysis.Unsatisfied.Select(u => u.Key).Should().Contain("Receiver/HttpPositionClient.GetPositionsAsync -> Sender /api/positions")
            .And.NotContain("Receiver/HttpPositionClient.GetOrdersAsync -> Sender /api/orders");
    }

    [Fact]
    public void 判定は受け手の本番が使う型の直列化を契約と数えない()
    {
        // 受け手の本番が別名で送り手の型（Limits）を使っている。その型を直列化しても、送り手の応答型の項目名は固定されない。
        var analysis = CrossServiceReadContractScan.Analyze(Tree(
            ReceiverTests(ContractTest("GetPositionsAsync", "Limits")),
            receiverExtra: "public sealed record Own(Sender::Sender.Limits Limits);"));

        analysis.Unsatisfied.Select(u => u.Operation).Should().Contain("GetPositionsAsync");
    }

    [Fact]
    public void 判定は送り手へ別名を付けていないテストを契約と数えない()
    {
        var analysis = CrossServiceReadContractScan.Analyze(Tree(
            ReceiverTests(ContractTest("GetPositionsAsync", "PositionView")), testProject: "<ProjectReference Include=\"..\\..\\Sender\\Sender.csproj\" />"));

        analysis.Unsatisfied.Select(u => u.Operation).Should().Contain("GetPositionsAsync");
    }

    [Fact]
    public void 判定は本文を読まない操作を単位にせず定数に置いたルートを使う操作へ帰す()
    {
        var analysis = CrossServiceReadContractScan.Analyze(Tree(ReceiverTests(ContractTest("GetPositionsAsync", "PositionView"))));

        analysis.SkippedWithoutBodyRead.Should().Equal("Receiver/HttpPositionClient.ConfirmAsync /api/confirm");
        analysis.Units.Select(u => u.Operation).Should().NotContain("ConfirmAsync");
        analysis.Units.Where(u => u.Route == "/api/orders").Select(u => u.Operation).Should().Equal(
            ["GetOrdersAsync"], "定数 → 非公開の Fetch → 公開の GetOrdersAsync と辿る");
    }

    [Fact]
    public void 判定はアダプタの外のルートとルートの無いアダプタと送り手側の固定の欠落を検出する()
    {
        var analysis = CrossServiceReadContractScan.Analyze(Tree(
            ReceiverTests(ContractTest("GetPositionsAsync", "PositionView")),
            receiverExtra: "public static class Stray { public const string Path = \"/api/positions\"; }",
            senderWireTest: false,
            extraAdapter: "public sealed class HttpExternal(HttpClient h) { public Task<string> GoAsync() => h.GetStringAsync(\"/v1/complete\"); }"));

        analysis.RouteLiteralsOutsideAdapters.Should().ContainSingle(s => s.StartsWith("Receiver/Features/Stray.cs", StringComparison.Ordinal));
        analysis.AdaptersWithoutRepositoryRoute.Should().Equal("Receiver/HttpExternal");
        analysis.ProvidersWithoutWireFormatTest.Should().Equal("Sender");
    }

    // ── 合成のツリー ──────────────────────────────────────────────────────

    private const string SenderProgram = """
        var app = builder.Build();
        var g = app.MapGroup("/api");
        g.MapGet("/positions", () => Results.Ok(new PositionView("A", 1)));
        g.MapGet("/orders", () => Results.Ok(new OrderView("A")));
        g.MapPost("/confirm", () => Results.Ok());
        public sealed record PositionView(string Symbol, int Quantity);
        public sealed record OrderView(string Symbol);
        public sealed record Limits(int Max);
        """;

    private const string SenderWireTest = """
        public class ReadContractWireFormatTests
        {
            [Fact]
            public async Task 本文() { var c = factory.CreateClient(); JsonNode.DeepEquals(a, b).Should().BeTrue(); }
        }
        """;

    private const string Adapter = """
        public sealed class HttpPositionClient(HttpClient http)
        {
            private const string OrdersPath = "/api/orders";

            public async Task<IReadOnlyList<Dto>?> GetPositionsAsync() =>
                await http.GetFromJsonAsync<IReadOnlyList<Dto>>("/api/positions");

            public Task<IReadOnlyList<Dto>?> GetOrdersAsync() => Fetch();

            public async Task<bool> ConfirmAsync()
            {
                using var response = await http.PostAsync("/api/confirm", null);
                return response.IsSuccessStatusCode;
            }

            private async Task<IReadOnlyList<Dto>?> Fetch() => await http.GetFromJsonAsync<IReadOnlyList<Dto>>(OrdersPath);

            private sealed record Dto(string? Symbol, int? Quantity);
        }
        """;

    private static string ContractTest(string operation, string senderType) => $$"""
        [Fact]
        public async Task 契約_{{operation}}()
        {
            var body = JsonSerializer.Serialize(new[] { new {{senderType}}("A", 1) }, Web);
            var r = await new HttpPositionClient(Client(body)).{{operation}}();
        }
        """;

    private static string ReceiverTests(params string[] methods) => $$"""
        extern alias SenderWorker;
        using SenderWorker::Sender;
        public class ContractTests
        {
        {{string.Join("\n", methods)}}
        }
        """;

    private static IReadOnlyList<ContractServiceTree> Tree(
        string receiverTests,
        string receiverExtra = "",
        string testProject = "<ProjectReference Include=\"..\\..\\Sender\\Sender.csproj\" Aliases=\"SenderWorker\" />",
        bool senderWireTest = true,
        string? extraAdapter = null)
    {
        var receiverProduction = new List<ContractSourceFile>
        {
            new("Infrastructure/ExternalServices/HttpPositionClient.cs", Adapter),
            new("Features/Stray.cs", receiverExtra),
        };
        if (extraAdapter is not null) receiverProduction.Add(new("Infrastructure/ExternalServices/HttpExternal.cs", extraAdapter));

        return
        [
            new ContractServiceTree(
                "Receiver", receiverProduction, [new("Tests/ContractTests.cs", receiverTests)], [$"<Project>{testProject}</Project>"]),
            new ContractServiceTree(
                "Sender",
                [new("Program.cs", SenderProgram)],
                senderWireTest ? [new("Tests/ReadContractWireFormatTests.cs", SenderWireTest)] : [],
                []),
        ];
    }
}
