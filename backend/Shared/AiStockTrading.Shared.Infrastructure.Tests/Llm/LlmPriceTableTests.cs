using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.Llm;

// NFR（費用）, FR-04, IADR-0122 決定2/3: 応答が名乗った実効モデルから単価を引く。
// fail-safe: 未知モデルは 0 でも既定ペアでもなく**表の最大単価**へ倒す（費用統制の危険側は過小計上のため）。
public class LlmPriceTableTests
{
    // IADR-0122 決定4 の投入値（換算率 163.71・2026-07 時点）。実運用の values-local.yaml と同じ表を使う。
    private static readonly (string Model, string? Input, string? Output)[] Catalog =
    [
        ("claude-fable-5", "1.637", "8.186"),
        ("claude-opus-5", "0.819", "4.093"),
        ("claude-opus-4-8", "0.819", "4.093"),
        ("claude-sonnet-5", "0.327", "1.637"),
        ("claude-haiku-4-5", "0.164", "0.819"),
    ];

    private static LlmPriceTable Table() => LlmPriceTable.From(Catalog);

    [Theory]
    [InlineData("claude-fable-5", 1.637, 8.186)]       // report-monthly
    [InlineData("claude-opus-5", 0.819, 4.093)]        // report-weekly・ゲートウェイ既定
    [InlineData("claude-opus-4-8", 0.819, 4.093)]      // ADR-0011 が意図する固定先
    [InlineData("claude-sonnet-5", 0.327, 1.637)]      // trade-decision・report-daily
    [InlineData("claude-haiku-4-5", 0.164, 0.819)]
    public void 実効モデルの単価を引く(string model, double input, double output)
    {
        Table().Resolve(model).Should().Be(new LlmPrice((decimal)input, (decimal)output));
    }

    // モデル名の大小はプロバイダ・ゲートウェイの表記に依存するため、一致判定で落とさない。
    [Fact]
    public void モデル名の大小は区別しない()
    {
        Table().Resolve("Claude-Sonnet-5").Should().Be(new LlmPrice(0.327m, 1.637m));
    }

    // fail-safe の中核: 未知モデルは最大単価（現行表では fable-5）。0 に倒すと月次上限が構造的に効かなくなる。
    [Theory]
    [InlineData("claude-sonnet-4-6")]
    [InlineData("gpt-5")]
    public void 表に無いモデルは最大単価へ倒す(string model)
    {
        Table().Resolve(model).Should().Be(new LlmPrice(1.637m, 8.186m));
    }

    // 応答がモデル名を名乗らない（上流未更新・部分写像の欠落）場合も未知と同じ扱い＝過小計上を作らない。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void モデル名が無い場合も最大単価へ倒す(string? model)
    {
        Table().Resolve(model).Should().Be(new LlmPrice(1.637m, 8.186m));
    }

    // 最大は「行」ではなく成分ごとに取る（入力が最大の行と出力が最大の行が別でも過小にしない）。
    [Fact]
    public void 最大単価は成分ごとに取る()
    {
        var table = LlmPriceTable.From([("a", "9", "1"), ("b", "1", "9")]);

        table.Resolve("unknown").Should().Be(new LlmPrice(9m, 9m));
    }

    // 解析できない・非正の行は表に載せない（→ 未知扱い＝最大単価）。誤設定を 0 円として通さない。
    [Theory]
    [InlineData("abc", "1.637")]
    [InlineData("0", "1.637")]
    [InlineData("-0.5", "1.637")]
    [InlineData("0.327", null)]
    [InlineData(null, null)]
    public void 単価が不正な行は表に載せない(string? input, string? output)
    {
        var table = LlmPriceTable.From([("claude-fable-5", "1.637", "8.186"), ("claude-sonnet-5", input, output)]);

        table.Resolve("claude-sonnet-5").Should().Be(new LlmPrice(1.637m, 8.186m));
    }

    // 小数点の記法はロケールに依存させない（InvariantCulture 固定）。
    [Fact]
    public void 単価は不変カルチャで解析する()
    {
        LlmPriceTable.From([("claude-sonnet-5", "0.327", "1.637")])
            .Resolve("claude-sonnet-5").Should().Be(new LlmPrice(0.327m, 1.637m));
    }

    // 後方互換: 表が空なら従来キー（global 単一ペア）へ倒れる。既存デプロイの挙動を変えない。
    [Fact]
    public void 表が空なら既定ペアへ倒れる()
    {
        var table = LlmPriceTable.From([], "0.819", "4.093");

        table.Resolve("claude-sonnet-5").Should().Be(new LlmPrice(0.819m, 4.093m));
        table.Resolve(null).Should().Be(new LlmPrice(0.819m, 4.093m));
    }

    // 全行が不正なら表は空とみなし、既定ペアへ倒れる（誤設定で最大単価に張り付かせない）。
    [Fact]
    public void 全行が不正なら既定ペアへ倒れる()
    {
        LlmPriceTable.From([("claude-sonnet-5", "abc", "xyz")], "0.819", "4.093")
            .Resolve("claude-sonnet-5").Should().Be(new LlmPrice(0.819m, 4.093m));
    }

    // 単価が一切設定されていなければ 0 円（IADR-0055 の安全既定・本番 values.yaml の現状）。
    [Fact]
    public void 何も設定が無ければ_0_円()
    {
        var table = LlmPriceTable.From([]);

        table.Resolve("claude-sonnet-5").Should().Be(LlmPrice.Zero);
    }

    // 既定ペアは入出力を独立に解析する（片側だけ不正でももう片側は活きる＝従来 ParsePricePer1k と同じ挙動）。
    [Fact]
    public void 既定ペアは入出力を独立に解析する()
    {
        LlmPriceTable.From([], "0.819", "abc").Resolve(null).Should().Be(new LlmPrice(0.819m, 0m));
    }

    // 表がある限り、既定ペアは未知モデルの単価にしない（表が真＝最大単価で倒す）。
    [Fact]
    public void 表があれば未知モデルに既定ペアを使わない()
    {
        LlmPriceTable.From([("claude-sonnet-5", "0.327", "1.637")], "99", "99")
            .Resolve("claude-opus-5").Should().Be(new LlmPrice(0.327m, 1.637m));
    }

    // 例外を投げない（計測は best-effort＝LLM 応答を壊さない・IADR-0055）。
    [Fact]
    public void 解決は例外を投げない()
    {
        var act = () => LlmPriceTable.From([("", null, null)]).Resolve(null);

        act.Should().NotThrow();
    }
}
