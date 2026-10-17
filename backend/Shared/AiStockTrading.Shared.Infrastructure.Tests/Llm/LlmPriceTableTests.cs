using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.Llm;

// NFR（費用）, FR-04, IADR-0122 決定2/3: 応答が名乗った実効モデルから単価を引く。
// fail-safe: 未知モデルは 0 でも既定ペアでもなく**専用の設定値「未知モデルの計上単価」**（既定 $10 / $50）へ倒す
// （費用統制の危険側は過小計上のため。NFR-13, ADR-0064 決定9, #1305, IADR-0553 で「表の最大単価」から改めた）。
public class LlmPriceTableTests
{
    // IADR-0122 決定4 の投入値（換算率 163.71）。実運用の values-local.yaml と同じ表（5.5 系の 3 行だけ）を使う。
    // #1295, IADR-0524: 5.5 系の単価（提供元の公表値 2026-10-10・planning#783）。haiku-5-5 は第 1 段（100,000 トークン以下）。
    // #1305, IADR-0553: `claude-fable-5` の行は外した（上限は専用の設定値が担う）。
    private static readonly (string Model, string? Input, string? Output)[] Catalog =
    [
        ("claude-opus-5-5", "0.655", "3.274"),
        ("claude-sonnet-5-5", "0.327", "1.637"),
        ("claude-haiku-5-5", "0.0164", "0.0819"),
    ];

    private static readonly LlmPrice DefaultUnknown = new(1.637m, 8.186m);

    private static LlmPriceTable Table() => LlmPriceTable.From(Catalog);

    [Theory]
    [InlineData("claude-opus-5-5", 0.655, 3.274)]        // report-weekly・report-monthly
    [InlineData("claude-sonnet-5-5", 0.327, 1.637)]      // trade-decision・report-daily
    [InlineData("claude-haiku-5-5", 0.0164, 0.0819)]     // trade-decision-screening・report-daily の第 2 候補
    public void 実効モデルの単価を引く(string model, double input, double output)
    {
        Table().Resolve(model, inputTokens: 0).Should().Be(new LlmPrice((decimal)input, (decimal)output));
    }

    // モデル名の大小はプロバイダ・ゲートウェイの表記に依存するため、一致判定で落とさない。
    [Fact]
    public void モデル名の大小は区別しない()
    {
        Table().Resolve("Claude-Sonnet-5-5", inputTokens: 0).Should().Be(new LlmPrice(0.327m, 1.637m));
    }

    // ---- 未知モデルの計上単価（NFR-13, FR-04, ADR-0064 決定9, #1305, IADR-0553）----

    // 受け入れ基準 1: 表が 5.5 系の 3 行だけのとき、表に無いモデル（旧世代・他社）は専用の設定値で計上する。
    [Theory]
    [InlineData("claude-opus-5")]
    [InlineData("gpt-5")]
    [InlineData("claude-fable-5")]   // 禁止モデルも単価表には無い（判定は割当表の側。単価は未知モデルと同じ）
    public void 表に無いモデルは専用の設定値で計上する(string model) =>
        LlmPriceTable.From(Catalog, unknownModelInputPer1kTokens: "2.5", unknownModelOutputPer1kTokens: "12.5")
            .Resolve(model, inputTokens: 0).Should().Be(new LlmPrice(2.5m, 12.5m));

    // 受け入れ基準 2: モデル名を名乗らない応答（null・空・空白）も 1 と同じ単価。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void モデル名が無い場合も専用の設定値で計上する(string? model) =>
        LlmPriceTable.From(Catalog, unknownModelInputPer1kTokens: "2.5", unknownModelOutputPer1kTokens: "12.5")
            .Resolve(model, inputTokens: 0).Should().Be(new LlmPrice(2.5m, 12.5m));

    // 受け入れ基準 3: 設定値が無ければ既定の $10 / $50（1.637 / 8.186 円/1k）。0 には倒さない。
    [Theory]
    [InlineData("gpt-5")]
    [InlineData(null)]
    public void 専用の設定値が無ければ既定の_10_50_ドルで計上する(string? model)
    {
        var table = Table();

        table.Resolve(model, inputTokens: 0).Should().Be(DefaultUnknown);
        table.UnknownModelPrice.Should().Be(DefaultUnknown);
        table.UnknownModelSettingRejected.Should().BeFalse();
    }

    // 既定値の出所を固定する: ADR-0064 決定9 の $10 / $50 per MTok を 05_trading-assumptions §6.1 の 163.71 円/USD で換算し、
    // 他の行と同じく小数第 3 位で四捨五入した値（10 × 163.71 ÷ 1000 = 1.6371、50 × 163.71 ÷ 1000 = 8.1855）。
    [Fact]
    public void 既定値は_10_50_ドルを_163_71_で換算した値()
    {
        const decimal rate = 163.71m;
        var expected = new LlmPrice(
            decimal.Round(10m * rate / 1000m, 3, MidpointRounding.AwayFromZero),
            decimal.Round(50m * rate / 1000m, 3, MidpointRounding.AwayFromZero));

        LlmPriceTable.DefaultUnknownModelPrice.Should().Be(expected);
        LlmPriceTable.DefaultUnknownModelPrice.Should().Be(DefaultUnknown);
    }

    // 受け入れ基準 4: 未知モデルの単価はどの行にも結びつかない。表の最大より高い行を足しても、低い表にしても変わらない。
    [Fact]
    public void 未知モデルの単価は表の行の足し外しで変わらない()
    {
        var withHigherRow = LlmPriceTable.From([.. Catalog, ("claude-future-6", "99", "99")]);
        var onlyCheapRow = LlmPriceTable.From([("claude-haiku-5-5", "0.0164", "0.0819")]);
        var withTier = LlmPriceTable.FromRows([new LlmPriceRow("b", "0.05", "0.1", "1000", "30", "60")]);

        withHigherRow.Resolve("gpt-5", inputTokens: 0).Should().Be(DefaultUnknown);
        onlyCheapRow.Resolve("gpt-5", inputTokens: 0).Should().Be(DefaultUnknown);
        withTier.Resolve("gpt-5", inputTokens: null).Should().Be(DefaultUnknown);
    }

    // 受け入れ基準 6（否定形）: 書かれているのに解析できない・0 以下の成分は、0 円にも表の最大にも倒さず既定値で計上し、不正を示す。
    // 成分は独立に読む（正しく書けた側は活かす）。
    [Theory]
    [InlineData("abc", "12.5", 1.637, 12.5)]
    [InlineData("0", "12.5", 1.637, 12.5)]
    [InlineData("-1", "12.5", 1.637, 12.5)]
    [InlineData("2.5", "1.5.0", 2.5, 8.186)]
    [InlineData("0", "0", 1.637, 8.186)]
    public void 専用の設定値が不正なら既定値で計上し不正を示す(string input, string output, double expectedInput, double expectedOutput)
    {
        var table = LlmPriceTable.From([.. Catalog, ("claude-future-6", "99", "99")], unknownModelInputPer1kTokens: input, unknownModelOutputPer1kTokens: output);

        table.Resolve("gpt-5", inputTokens: 0).Should().Be(new LlmPrice((decimal)expectedInput, (decimal)expectedOutput));
        table.UnknownModelSettingRejected.Should().BeTrue();
    }

    // 未設定（null・空・空白）は不正ではない（既定値が意図した値）。起動時の警告を出さない。
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("  ", null)]
    public void 専用の設定値が未設定なら不正を示さない(string? input, string? output) =>
        LlmPriceTable.From(Catalog, unknownModelInputPer1kTokens: input, unknownModelOutputPer1kTokens: output)
            .UnknownModelSettingRejected.Should().BeFalse();

    // 表が空なら従来キーへ倒れる（解決の順序は変えない）。専用の設定値は表が非空のときだけ効く。
    [Fact]
    public void 表が空なら専用の設定値があっても既定ペアへ倒れる() =>
        LlmPriceTable.From([], "0.819", "4.093", "2.5", "12.5")
            .Resolve("gpt-5", inputTokens: 0).Should().Be(new LlmPrice(0.819m, 4.093m));

    // IsEffectivelyZero の意味は変えない（#817・#1197 / IADR-0499）: 表が空なら設定値は使われず全呼び出しが 0 円なので、
    // 設定値だけの構成を「単価がある」と読まない（本番の起動拒否を設定値で迂回させない）。
    [Fact]
    public void IsEffectivelyZero_専用の設定値だけでは真のまま() =>
        LlmPriceTable.From([], unknownModelInputPer1kTokens: "2.5", unknownModelOutputPer1kTokens: "12.5")
            .IsEffectivelyZero.Should().BeTrue();

    // 解析できない・非正の行は表に載せない（→ 未知扱い＝未知モデルの計上単価）。誤設定を 0 円として通さない。
    [Theory]
    [InlineData("abc", "1.637")]
    [InlineData("0", "1.637")]
    [InlineData("-0.5", "1.637")]
    [InlineData("0.327", null)]
    [InlineData(null, null)]
    public void 単価が不正な行は表に載せない(string? input, string? output)
    {
        var table = LlmPriceTable.From([("claude-opus-5-5", "0.655", "3.274"), ("claude-sonnet-5-5", input, output)]);

        table.Resolve("claude-sonnet-5-5", inputTokens: 0).Should().Be(DefaultUnknown);
    }

    // 小数点の記法はロケールに依存させない（InvariantCulture 固定）。
    [Fact]
    public void 単価は不変カルチャで解析する()
    {
        LlmPriceTable.From([("claude-sonnet-5-5", "0.327", "1.637")])
            .Resolve("claude-sonnet-5-5", inputTokens: 0).Should().Be(new LlmPrice(0.327m, 1.637m));
    }

    // 後方互換: 表が空なら従来キー（global 単一ペア）へ倒れる。既存デプロイの挙動を変えない。
    [Fact]
    public void 表が空なら既定ペアへ倒れる()
    {
        var table = LlmPriceTable.From([], "0.819", "4.093");

        table.Resolve("claude-sonnet-5-5", inputTokens: 0).Should().Be(new LlmPrice(0.819m, 4.093m));
        table.Resolve(null, inputTokens: 0).Should().Be(new LlmPrice(0.819m, 4.093m));
    }

    // 全行が不正なら表は空とみなし、既定ペアへ倒れる（従来キーの構成を未知モデルの計上単価で上書きしない）。
    [Fact]
    public void 全行が不正なら既定ペアへ倒れる()
    {
        LlmPriceTable.From([("claude-sonnet-5-5", "abc", "xyz")], "0.819", "4.093")
            .Resolve("claude-sonnet-5-5", inputTokens: 0).Should().Be(new LlmPrice(0.819m, 4.093m));
    }

    // 単価が一切設定されていなければ 0 円（IADR-0055 の安全既定・本番 values.yaml の現状）。解決は 0 のまま変えない。
    // LLM ゲートウェイを構成した配備（Production）は起動時に止まる（LlmPricingStartupGuard・IADR-0499 / #1197）。
    [Fact]
    public void 何も設定が無ければ_0_円()
    {
        var table = LlmPriceTable.From([]);

        table.Resolve("claude-sonnet-5-5", inputTokens: 0).Should().Be(LlmPrice.Zero);
    }

    // 既定ペアは入出力を独立に解析する（片側だけ不正でももう片側は活きる＝従来 ParsePricePer1k と同じ挙動）。
    [Fact]
    public void 既定ペアは入出力を独立に解析する()
    {
        LlmPriceTable.From([], "0.819", "abc").Resolve(null, inputTokens: 0).Should().Be(new LlmPrice(0.819m, 0m));
    }

    // 表がある限り、既定ペアは未知モデルの単価にしない（表が真＝未知モデルの計上単価で倒す）。
    [Fact]
    public void 表があれば未知モデルに既定ペアを使わない()
    {
        LlmPriceTable.From([("claude-sonnet-5-5", "0.327", "1.637")], "99", "99")
            .Resolve("claude-opus-5-5", inputTokens: 0).Should().Be(DefaultUnknown);
    }

    // #817: env 名にハイフンを入れるとイメージの `sh -c` 起動（dash）が非識別子として落とす。
    // 構成は `claude_sonnet_5_5` のアンダースコア形で書き、応答が名乗る `claude-sonnet-5-5` と同一視する。
    [Theory]
    [InlineData("claude_opus_5_5", "claude-opus-5-5")]
    [InlineData("claude_sonnet_5_5", "claude-sonnet-5-5")]
    [InlineData("claude_haiku_5_5", "Claude-Haiku-5-5")]
    public void アンダースコアのキーはハイフンのモデル名に一致する(string key, string model)
    {
        // 未知扱い（未知モデルの計上単価＝1.637 / 8.186）に落ちたのではなく一致したことを、値の違いで区別する。
        LlmPriceTable.From([("other_model", "9", "9"), (key, "0.327", "1.637")])
            .Resolve(model, inputTokens: 0).Should().Be(new LlmPrice(0.327m, 1.637m));
    }

    // 逆向き（ハイフンのキー × アンダースコアの名乗り）も同一視する（照合は双方を正規化する）。
    [Fact]
    public void ハイフンのキーはアンダースコアのモデル名にも一致する()
    {
        Table().Resolve("claude_sonnet_5_5", inputTokens: 0).Should().Be(new LlmPrice(0.327m, 1.637m));
    }

    // 正規化しても fail-safe は不変: 表が非空なら未知モデルは未知モデルの計上単価。
    [Fact]
    public void アンダースコアの表でも未知モデルは未知モデルの計上単価へ倒す()
    {
        var table = LlmPriceTable.From([("claude_opus_5_5", "0.655", "3.274"), ("claude_sonnet_5_5", "0.327", "1.637")]);

        table.Resolve("claude-sonnet-4-6", inputTokens: 0).Should().Be(DefaultUnknown);
        table.Resolve(null, inputTokens: 0).Should().Be(DefaultUnknown);
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
        var act = () => LlmPriceTable.From([("", null, null)]).Resolve(null, inputTokens: 0);

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

    // 第 2 段を持たない行は入力トークン数に依らない（従来の 1 段の挙動）。
    [Fact]
    public void 第2段の無い行は入力トークン数に依らない() =>
        TieredTable().Resolve("claude-sonnet-5-5", 500_000).Should().Be(new LlmPrice(0.327m, 1.637m));

    // 未知モデルは入力トークン数に依らず未知モデルの計上単価（#1305, IADR-0553。第 2 段の有無にも依らない）。
    [Theory]
    [InlineData(0)]
    [InlineData(500_000)]
    public void 未知モデルは入力トークン数に依らず未知モデルの計上単価へ倒す(int inputTokens) =>
        TieredTable().Resolve("claude-haiku-4-5", inputTokens).Should().Be(DefaultUnknown);

    // 🔴 第 2 段の誤設定（キーの一部欠け・解析不能・非正）は行ごと載せない（→ 未知モデルの計上単価）。
    // 長いプロンプトを第 1 段の安い単価で通さない（過小計上を作らない）。
    [Theory]
    [InlineData("100000", "0.0819", null)]
    [InlineData("100000", null, "0.409")]
    [InlineData(null, "0.0819", "0.409")]
    [InlineData("abc", "0.0819", "0.409")]
    [InlineData("0", "0.0819", "0.409")]
    [InlineData("100000", "-1", "0.409")]
    public void 第2段の誤設定の行は載せず未知モデル扱いになる(string? threshold, string? longInput, string? longOutput) =>
        TieredTable(threshold, longInput, longOutput).Resolve("claude-haiku-5-5", 10).Should().Be(DefaultUnknown);

    // 費用 = 入力÷1000×単価 + 出力÷1000×単価（段は要求ごとに決まる）。
    [Fact]
    public void 第2段の単価で費用を計算する()
    {
        var price = TieredTable().Resolve("claude-haiku-5-5", 120_000);

        LlmPricing.Compute(120_000, 1_000, price).Should().Be((120m * 0.0819m) + (1m * 0.409m));
    }

    // ---- 入力トークン数が取れない要求（NFR-13, FR-04, ADR-0064 決定4, #1306, IADR-0550）----

    // 🔴 受け入れ基準 1・2: 取れない（null）要求は第 2 段（高い側）で引く。0 と読むと第 1 段＝過小計上（IADR-0122 の危険側）。
    [Fact]
    public void 入力トークン数が取れない要求は第2段の単価で引く() =>
        TieredTable().Resolve("claude-haiku-5-5", inputTokens: null).Should().Be(new LlmPrice(0.0819m, 0.409m));

    // 受け入れ基準 3: 取れている要求は従来どおり（100,000 ちょうどは第 1 段・100,001 は第 2 段）。取れない要求と並べて固定する。
    [Theory]
    [InlineData(100_000, 0.0164, 0.0819)]
    [InlineData(100_001, 0.0819, 0.409)]
    public void 取れている要求は従来どおり閾値で引き分ける(int inputTokens, double input, double output) =>
        TieredTable().Resolve("claude-haiku-5-5", (int?)inputTokens).Should().Be(new LlmPrice((decimal)input, (decimal)output));

    // 受け入れ基準 4: 第 2 段を持たない行・未知モデル・表が空のときは、取れなくても単価は従来と変わらない（段の選択だけを変える）。
    [Fact]
    public void 第2段の無い行と未知モデルと空の表は取れなくても従来どおり()
    {
        TieredTable().Resolve("claude-sonnet-5-5", inputTokens: null).Should().Be(new LlmPrice(0.327m, 1.637m));
        TieredTable().Resolve("claude-haiku-4-5", inputTokens: null).Should().Be(DefaultUnknown);
        TieredTable().Resolve(null, inputTokens: null).Should().Be(DefaultUnknown);
        LlmPriceTable.From([], "0.819", "4.093").Resolve("claude-haiku-5-5", inputTokens: null)
            .Should().Be(new LlmPrice(0.819m, 4.093m));
    }

    // 🔴 注意（#1306）: 金額の式の入力トークン数は 0 のまま（推定しない）。変わるのは出力の単価が第 2 段になることだけ。
    [Fact]
    public void 取れない要求の費用は入力を足さず出力を第2段の単価で計算する()
    {
        var price = TieredTable().Resolve("claude-haiku-5-5", inputTokens: null);

        LlmPricing.Compute(0, 1_000, price).Should().Be(0.409m);
    }

    // 受け入れ基準 6（形の固定）: 段の判定は「取れない」を表せる型で受ける。int へ戻すと呼び出し側が 0 へ潰すしかなくなる。
    [Fact]
    public void 段の判定は取れないを表せる型で入力トークン数を受ける() =>
        typeof(LlmPriceTable).GetMethod(nameof(LlmPriceTable.Resolve))!.GetParameters()[1].ParameterType
            .Should().Be(typeof(int?));
}
