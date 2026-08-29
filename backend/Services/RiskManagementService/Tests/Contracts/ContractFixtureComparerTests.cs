using AiStockTrading.TestSupport.ContractFixtures;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests.Contracts;

// FR-10, FR-20, #389, IADR-0146 決定6: **契約比較器の否定形テスト（検査器そのものの試験）。**
//
// 検査器の危険な壊れ方は「赤くなるべきときに緑のまま」である。フィクスチャ突合が正規化のしすぎ・
// 比較の甘さで改名を吸収してしまうと、**CI は緑のままフロントが壊れる**——本 issue が起きた状態へ
// そのまま戻る。よって「一致すること」の確認より「**ずれを見逃さないこと**」の確認を重く置く
// （否定形を正の確認と同数以上・IADR-0143 / IADR-0145 の先例）。
public class ContractFixtureComparerTests
{
    // 実応答を模した最小の契約（#389 の 3 キーを含む形）。
    private const string Baseline = """
        {
          "limits": { "maxOrderAmountRatio": 0.25, "maxDailyOrderAmountRatio": 1.50 },
          "stage": { "stage": 0, "mode": 0, "capitalCapRatio": 0.00 },
          "history": [ { "actor": "test-owner", "changedAt": "2026-08-05T09:00:00.1234567+00:00" } ]
        }
        """;

    // ---- 否定形（ずれを見逃さないこと。5 件） ----

    [Fact]
    public void トップレベルのキー改名を不一致として検出する()
    {
        // #333 の実例: capitalCap → capitalCapRatio。名前だけが変わって値の形は同じ、という最も見逃しやすい差。
        var renamed = Baseline.Replace("\"stage\": {", "\"stageSettings\": {", StringComparison.Ordinal);

        ContractFixtureComparer.Matches(Baseline, renamed, out var difference).Should().BeFalse();
        difference.Should().NotBeEmpty();
    }

    [Fact]
    public void ネストしたキーの改名を不一致として検出する()
    {
        // #329 の実例そのもの（RiskLimitSettings.MaxOrderAmountRatio）。ネストの奥にある改名も見逃さない。
        var renamed = Baseline.Replace("maxOrderAmountRatio", "maxOrderAmount", StringComparison.Ordinal);

        ContractFixtureComparer.Matches(Baseline, renamed, out var difference).Should().BeFalse();
        difference.Should().NotBeEmpty();
    }

    [Fact]
    public void キーの欠落を不一致として検出する()
    {
        // プロパティを削っただけ（改名を伴わない）でもフロントは undefined を読む。
        var dropped = """
            {
              "limits": { "maxOrderAmountRatio": 0.25 },
              "stage": { "stage": 0, "mode": 0, "capitalCapRatio": 0.00 },
              "history": [ { "actor": "test-owner", "changedAt": "2026-08-05T09:00:00.1234567+00:00" } ]
            }
            """;

        ContractFixtureComparer.Matches(Baseline, dropped, out var difference).Should().BeFalse();
        difference.Should().NotBeEmpty();
    }

    [Fact]
    public void 値の型の変更を不一致として検出する()
    {
        // decimal → string（enum の JsonStringEnumConverter 導入等で実際に起こり得る）。
        var retyped = Baseline.Replace("\"stage\": 0", "\"stage\": \"Stage0Verification\"", StringComparison.Ordinal);

        ContractFixtureComparer.Matches(Baseline, retyped, out var difference).Should().BeFalse();
        difference.Should().NotBeEmpty();
    }

    [Fact]
    public void 配列要素のキー改名を不一致として検出する()
    {
        // 履歴・遷移履歴は配列であり、要素の中の改名も検出できなければ意味がない。
        var renamed = Baseline.Replace("\"actor\"", "\"changedBy\"", StringComparison.Ordinal);

        ContractFixtureComparer.Matches(Baseline, renamed, out var difference).Should().BeFalse();
        difference.Should().NotBeEmpty();
    }

    // ---- 正の確認（正規化が「吸収してよい差」だけを吸収すること。5 件） ----

    [Fact]
    public void 同一の契約は一致とみなす()
    {
        ContractFixtureComparer.Matches(Baseline, Baseline, out var difference).Should().BeTrue();
        difference.Should().BeEmpty();
    }

    [Fact]
    public void 空白と改行の差は吸収する()
    {
        // 実応答は 1 行、フィクスチャは整形済みである。整形の差で赤くなると再生成が常用され機構が死ぬ。
        var minified = """
            {"limits":{"maxOrderAmountRatio":0.25,"maxDailyOrderAmountRatio":1.50},"stage":{"stage":0,"mode":0,"capitalCapRatio":0.00},"history":[{"actor":"test-owner","changedAt":"2026-08-05T09:00:00.1234567+00:00"}]}
            """;

        ContractFixtureComparer.Matches(Baseline, minified, out _).Should().BeTrue();
    }

    [Fact]
    public void 日時の値の差は吸収する()
    {
        // DateTimeOffset は実行のたびに変わる。ここを吸収しないとフィクスチャが毎回不安定になる。
        var later = Baseline.Replace(
            "2026-08-05T09:00:00.1234567+00:00", "2027-01-31T23:59:59.9999999+09:00", StringComparison.Ordinal);

        ContractFixtureComparer.Matches(Baseline, later, out _).Should().BeTrue();
    }

    [Fact]
    public void 日時の正規化はキー名ではなく値の形で判定する()
    {
        // IADR-0146 決定4: キー名の登録簿を作らない。見たことのないキーに載った日時も正規化される。
        const string unknownKey = """{ "somethingObservedAtUtc": "2026-08-05T09:00:00.1234567+00:00" }""";
        const string unknownKeyLater = """{ "somethingObservedAtUtc": "2030-12-31T00:00:00.0000000+00:00" }""";

        ContractFixtureComparer.Matches(unknownKey, unknownKeyLater, out _).Should().BeTrue();
        ContractFixtureComparer.Normalize(unknownKey).Should().Contain(ContractFixtureComparer.NormalizedTimestamp);
    }

    [Theory]
    // 正規化する（ISO 日時）。
    [InlineData("2026-08-05T09:00:00.1234567+00:00", true)]
    [InlineData("2026-08-05T09:00:00Z", true)]
    // 正規化しない。DateOnly（registeredOn）は既定値由来の定数であり、吸収すると契約の情報を無意味に捨てる。
    [InlineData("2026-08-05", false)]
    // 単なる文字列（銘柄コード・理由・承認者）を日時と誤認すると、値の変化を丸ごと見逃す。
    [InlineData("test-owner", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 日時判定は値の形だけで行い日付や一般の文字列を巻き込まない(string? text, bool expected)
    {
        ContractFixtureComparer.IsIsoTimestamp(text).Should().Be(expected);
    }
}
