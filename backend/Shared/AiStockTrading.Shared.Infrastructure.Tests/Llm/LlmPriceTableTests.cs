using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.Llm;

// NFR（費用）, FR-04, IADR-0122 決定2/3: 応答が名乗った実効モデルから単価を引く。
// fail-safe: 未知モデルは 0 でも既定ペアでもなく**表の最大単価**へ倒す（費用統制の危険側は過小計上のため）。
public class LlmPriceTableTests
{
    // IADR-0122 決定4 の投入値（換算率 163.71）。実運用の values-local.yaml と同じ表を使う。
    // #1295, IADR-0524: 5.5 系の単価（提供元の公表値 2026-10-10・planning#783）。haiku-5-5 は第 1 段（100,000 トークン以下）。
    private static readonly (string Model, string? Input, string? Output)[] Catalog =
    [
        ("claude-fable-5", "1.637", "8.186"),
        ("claude-opus-5-5", "0.655", "3.274"),
        ("claude-opus-4-8", "0.819", "4.093"),
        ("claude-sonnet-5-5", "0.327", "1.637"),
        ("claude-haiku-5-5", "0.0164", "0.0819"),
    ];

    private static LlmPriceTable Table() => LlmPriceTable.From(Catalog);

    [Theory]
    [InlineData("claude-fable-5", 1.637, 8.186)]       // 禁止モデル（fail-safe 上限値）
    [InlineData("claude-opus-5-5", 0.655, 3.274)]        // report-weekly・report-monthly
    [InlineData("claude-opus-4-8", 0.819, 4.093)]      // ADR-0011 が意図する固定先
    [InlineData("claude-sonnet-5-5", 0.327, 1.637)]      // trade-decision・report-daily
    [InlineData("claude-haiku-5-5", 0.0164, 0.0819)]     // trade-decision-screening・report-daily の第 2 候補
    public void 実効モデルの単価を引く(string model, double input, double output)
    {
        Table().Resolve(model).Should().Be(new LlmPrice((decimal)input, (decimal)output));
    }

    // モデル名の大小はプロバイダ・ゲートウェイの表記に依存するため、一致判定で落とさない。
    [Fact]
    public void モデル名の大小は区別しない()
    {
        Table().Resolve("Claude-Sonnet-5-5").Should().Be(new LlmPrice(0.327m, 1.637m));
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
        var table = LlmPriceTable.From([("claude-fable-5", "1.637", "8.186"), ("claude-sonnet-5-5", input, output)]);

        table.Resolve("claude-sonnet-5-5").Should().Be(new LlmPrice(1.637m, 8.186m));
    }

    // 小数点の記法はロケールに依存させない（InvariantCulture 固定）。
    [Fact]
    public void 単価は不変カルチャで解析する()
    {
        LlmPriceTable.From([("claude-sonnet-5-5", "0.327", "1.637")])
            .Resolve("claude-sonnet-5-5").Should().Be(new LlmPrice(0.327m, 1.637m));
    }

    // 後方互換: 表が空なら従来キー（global 単一ペア）へ倒れる。既存デプロイの挙動を変えない。
    [Fact]
    public void 表が空なら既定ペアへ倒れる()
    {
        var table = LlmPriceTable.From([], "0.819", "4.093");

        table.Resolve("claude-sonnet-5-5").Should().Be(new LlmPrice(0.819m, 4.093m));
        table.Resolve(null).Should().Be(new LlmPrice(0.819m, 4.093m));
    }

    // 全行が不正なら表は空とみなし、既定ペアへ倒れる（誤設定で最大単価に張り付かせない）。
    [Fact]
    public void 全行が不正なら既定ペアへ倒れる()
    {
        LlmPriceTable.From([("claude-sonnet-5-5", "abc", "xyz")], "0.819", "4.093")
            .Resolve("claude-sonnet-5-5").Should().Be(new LlmPrice(0.819m, 4.093m));
    }

    // 単価が一切設定されていなければ 0 円（IADR-0055 の安全既定・本番 values.yaml の現状）。解決は 0 のまま変えない。
    // LLM ゲートウェイを構成した配備（Production）は起動時に止まる（LlmPricingStartupGuard・IADR-0499 / #1197）。
    [Fact]
    public void 何も設定が無ければ_0_円()
    {
        var table = LlmPriceTable.From([]);

        table.Resolve("claude-sonnet-5-5").Should().Be(LlmPrice.Zero);
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
        LlmPriceTable.From([("claude-sonnet-5-5", "0.327", "1.637")], "99", "99")
            .Resolve("claude-opus-5-5").Should().Be(new LlmPrice(0.327m, 1.637m));
    }

    // #817: env 名にハイフンを入れるとイメージの `sh -c` 起動（dash）が非識別子として落とす。
    // 構成は `claude_sonnet_5_5` のアンダースコア形で書き、応答が名乗る `claude-sonnet-5-5` と同一視する。
    [Theory]
    [InlineData("claude_fable_5", "claude-fable-5")]
    [InlineData("claude_opus_5_5", "claude-opus-5-5")]
    [InlineData("claude_opus_4_8", "claude-opus-4-8")]
    [InlineData("claude_sonnet_5_5", "claude-sonnet-5-5")]
    [InlineData("claude_haiku_5_5", "Claude-Haiku-5-5")]
    public void アンダースコアのキーはハイフンのモデル名に一致する(string key, string model)
    {
        // 表に別の高い行を置き、未知扱い（最大単価）に落ちたのではなく一致したことを区別する。
        LlmPriceTable.From([("other_model", "9", "9"), (key, "0.327", "1.637")])
            .Resolve(model).Should().Be(new LlmPrice(0.327m, 1.637m));
    }

    // 逆向き（ハイフンのキー × アンダースコアの名乗り）も同一視する（照合は双方を正規化する）。
    [Fact]
    public void ハイフンのキーはアンダースコアのモデル名にも一致する()
    {
        Table().Resolve("claude_sonnet_5_5").Should().Be(new LlmPrice(0.327m, 1.637m));
    }

    // 正規化しても fail-safe は不変: 表が非空なら未知モデルは成分ごとの最大単価。
    [Fact]
    public void アンダースコアの表でも未知モデルは最大単価へ倒す()
    {
        var table = LlmPriceTable.From([("claude_fable_5", "1.637", "8.186"), ("claude_sonnet_5_5", "0.327", "1.637")]);

        table.Resolve("claude-sonnet-4-6").Should().Be(new LlmPrice(1.637m, 8.186m));
        table.Resolve(null).Should().Be(new LlmPrice(1.637m, 8.186m));
    }

    // #817 fail-loud: 表が空 かつ 従来キーも無い＝全呼び出しが 0 円になる構成を判定できる（起動時警告の入力）。
    [Fact]
    public void IsEffectivelyZero_表も既定ペアも無ければ真()
    {
        LlmPriceTable.From([]).IsEffectivelyZero.Should().BeTrue();
    }

    [Fact]
    public void IsEffectivelyZero_全行が不正で既定ペアも無ければ真()
    {
        LlmPriceTable.From([("claude_sonnet_5_5", "abc", "0")]).IsEffectivelyZero.Should().BeTrue();
    }

    [Fact]
    public void IsEffectivelyZero_表があれば偽()
    {
        Table().IsEffectivelyZero.Should().BeFalse();
    }

    [Theory]
    [InlineData("0.819", "4.093")]
    [InlineData("0.819", null)]
    [InlineData(null, "4.093")]
    public void IsEffectivelyZero_既定ペアのどちらかがあれば偽(string? input, string? output)
    {
        LlmPriceTable.From([], input, output).IsEffectivelyZero.Should().BeFalse();
    }

    // 例外を投げない（計測は best-effort＝LLM 応答を壊さない・IADR-0055）。
    [Fact]
    public void 解決は例外を投げない()
    {
        var act = () => LlmPriceTable.From([("", null, null)]).Resolve(null);

        act.Should().NotThrow();
    }

    // ---- プロンプト長の 2 段（#1295, IADR-0524。claude-haiku-5-5: 入力 100,000 トークン以下 $0.10/$0.50・超 $0.50/$2.50）----

    private static LlmPriceTable TieredTable(string? threshold = "100000", string? longInput = "0.0819", string? longOutput = "0.409") =>
        LlmPriceTable.FromRows(
        [
            new LlmPriceRow("claude-opus-5-5", "0.655", "3.274"),
            new LlmPriceRow("claude-sonnet-5-5", "0.327", "1.637"),
            new LlmPriceRow("claude_haiku_5_5", "0.0164", "0.0819", threshold, longInput, longOutput),
        ]);

    // 境界は「閾値を**超える**と第 2 段」（100,000 ちょうどは第 1 段）。
    [Theory]
    [InlineData(0, 0.0164, 0.0819)]
    [InlineData(99_999, 0.0164, 0.0819)]
    [InlineData(100_000, 0.0164, 0.0819)]
    [InlineData(100_001, 0.0819, 0.409)]
    [InlineData(900_000, 0.0819, 0.409)]
    public void 第2段を持つ行は入力トークン数で単価を引き分ける(int inputTokens, double input, double output) =>
        TieredTable().Resolve("claude-haiku-5-5", inputTokens).Should().Be(new LlmPrice((decimal)input, (decimal)output));

    // 入力トークン数を渡さない解決は第 1 段（従来の 1 段の挙動）。第 2 段を持たない行は入力トークン数に依らない。
    [Fact]
    public void 入力トークン数を渡さない解決と第2段の無い行は従来どおり()
    {
        TieredTable().Resolve("claude-haiku-5-5").Should().Be(new LlmPrice(0.0164m, 0.0819m));
        TieredTable().Resolve("claude-sonnet-5-5", 500_000).Should().Be(new LlmPrice(0.327m, 1.637m));
    }

    // 未知モデルは従来どおり表の成分ごとの最大（第 2 段も母集合に含める＝過小にしない）。
    [Theory]
    [InlineData(0)]
    [InlineData(500_000)]
    public void 未知モデルは入力トークン数に依らず最大単価へ倒す(int inputTokens) =>
        TieredTable().Resolve("claude-haiku-4-5", inputTokens).Should().Be(new LlmPrice(0.655m, 3.274m));

    [Fact]
    public void 第2段だけが最大でも未知モデルの単価を過小にしない()
    {
        var table = LlmPriceTable.FromRows(
        [
            new LlmPriceRow("a", "0.1", "0.2"),
            new LlmPriceRow("b", "0.05", "0.1", "1000", "0.3", "0.6"),
        ]);

        table.Resolve("unknown").Should().Be(new LlmPrice(0.3m, 0.6m));
    }

    // 🔴 第 2 段の誤設定（キーの一部欠け・解析不能・非正）は行ごと載せない（→ 未知モデル＝最大単価）。
    // 長いプロンプトを第 1 段の安い単価で通さない（過小計上を作らない）。
    [Theory]
    [InlineData("100000", "0.0819", null)]
    [InlineData("100000", null, "0.409")]
    [InlineData(null, "0.0819", "0.409")]
    [InlineData("abc", "0.0819", "0.409")]
    [InlineData("0", "0.0819", "0.409")]
    [InlineData("100000", "-1", "0.409")]
    public void 第2段の誤設定の行は載せず未知モデル扱いになる(string? threshold, string? longInput, string? longOutput) =>
        TieredTable(threshold, longInput, longOutput).Resolve("claude-haiku-5-5", 10).Should().Be(new LlmPrice(0.655m, 3.274m));

    // 費用 = 入力÷1000×単価 + 出力÷1000×単価（段は要求ごとに決まる）。
    [Fact]
    public void 第2段の単価で費用を計算する()
    {
        var price = TieredTable().Resolve("claude-haiku-5-5", 120_000);

        LlmPricing.Compute(120_000, 1_000, price).Should().Be((120m * 0.0819m) + (1m * 0.409m));
    }
}
