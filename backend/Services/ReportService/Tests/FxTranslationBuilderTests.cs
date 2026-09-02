using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-16, #611, 04_report-templates §数値の定義（為替差損益）, 05_trading-assumptions §3, IADR-0285 決定3・決定4:
// 期間の約定から為替差損益の明細を組み立てる純関数。**何を明細にするか**（決済＝決済時レートへ・期末残＝期末レートへ）と、
// 供給できない条件（未記録の約定・期末レート無し）の向きを固定する。
public class FxTranslationBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 3, 14, 30, 0, TimeSpan.Zero);
    private static readonly PeriodEndFxRate End160 = new(160m, new DateOnly(2026, 8, 28));

    private static PeriodTradeFill Us(TradeSide side, int qty, decimal price, decimal? rate, int minutes = 0, string symbol = "AAPL") =>
        new(symbol, Market.UnitedStates, side, PositionEffect.Open, qty, price, T0.AddMinutes(minutes),
            FxRateBaseToDisplay: rate);

    private static PeriodTradeFill Jp(TradeSide side, int qty, decimal price, decimal? rate = null, int minutes = 0) =>
        new("7203", Market.Japan, side, PositionEffect.Open, qty, price, T0.AddMinutes(minutes), FxRateBaseToDisplay: rate);

    // ---- 対象の選別 ----

    // 計画「円換算 | **米国株は**…円換算」: 日本株は円建てで円換算を要しない（差損益を生まない）。
    [Fact]
    public void 日本株は対象にしない()
    {
        var result = FxTranslationBuilder.Build([Jp(TradeSide.Buy, 100, 2_500m, rate: 150m)], End160);

        result.Summary.Should().NotBeNull();
        result.Summary!.EntryCount.Should().Be(0);
        result.Summary.TranslationGainJpy.Should().Be(0m);
    }

    // 対象約定が 1 件も無ければ「0 円（明細 0 件）」——事実であり未供給ではない。期末レートが無くても同じ。
    [Fact]
    public void USD建て約定が無ければ期末レートが無くても0件で集計する()
    {
        var result = FxTranslationBuilder.Build([], periodEnd: null);

        result.Summary.Should().Be(new FxTranslationSummary(0m, 0));
        result.UnrecordedFillCount.Should().Be(0);
    }

    // ---- 決済（決済時レートへの再測定） ----

    // $1,000 を 150 円で認識し、155 円のときに全量決済 → 1,000 × (155 − 150) = +5,000 円。
    // 期末に建玉が残らないため期末レートは**要らない**（null でも集計できる）。
    [Fact]
    public void 決済は認識時レートから決済時レートへ再測定する()
    {
        var fills = new[]
        {
            Us(TradeSide.Buy, 10, 100m, rate: 150m),
            Us(TradeSide.Sell, 10, 110m, rate: 155m, minutes: 10),
        };

        var result = FxTranslationBuilder.Build(fills, periodEnd: null);

        result.Summary.Should().Be(new FxTranslationSummary(5_000m, 1));
    }

    // 🔴 退けた案（約定ごとに約定代金を明細にする）との差: 同じ建玉の両脚を二重に数えない。
    // 約定ごとなら 1,000×(160−150) ＋ 1,100×(160−155) ＝ 15,500 円になるが、決済で確定した差損益は 5,000 円である。
    [Fact]
    public void 期間内に建てて決済した建玉を二重に数えない()
    {
        var fills = new[]
        {
            Us(TradeSide.Buy, 10, 100m, rate: 150m),
            Us(TradeSide.Sell, 10, 110m, rate: 155m, minutes: 10),
        };

        var result = FxTranslationBuilder.Build(fills, End160);

        result.Summary!.TranslationGainJpy.Should().Be(5_000m);
        result.Summary.EntryCount.Should().Be(1);
        // 期末の再測定を使っていないため期末レートは載らない。
        result.Summary.PeriodEndRate.Should().BeNull();
    }

    // 一部決済: 減少分の原価だけを再測定し、残りは期末レートで再測定する。
    // 買 20 株 @$100（150 円）→ 売 5 株（155 円）: 500×5 = +2,500 円。残 15 株 $1,500: 1,500×(160−150) = +15,000 円。
    [Fact]
    public void 一部決済は減少分だけを決済時レートへ再測定し残りを期末レートへ再測定する()
    {
        var fills = new[]
        {
            Us(TradeSide.Buy, 20, 100m, rate: 150m),
            Us(TradeSide.Sell, 5, 120m, rate: 155m, minutes: 10),
        };

        var result = FxTranslationBuilder.Build(fills, End160);

        result.Summary.Should().Be(new FxTranslationSummary(17_500m, 2, 160m, new DateOnly(2026, 8, 28)));
    }

    // 建て増しは認識時レートを**原価加重**で平均する。買 10 @$100（150 円）＋買 10 @$100（160 円）→ 平均 155 円。
    // 期末 160 円で $2,000 を再測定: 2,000 × (160 − 155) = +10,000 円。
    [Fact]
    public void 建て増しは認識時レートを原価加重で平均する()
    {
        var fills = new[]
        {
            Us(TradeSide.Buy, 10, 100m, rate: 150m),
            Us(TradeSide.Buy, 10, 100m, rate: 160m, minutes: 10),
        };

        var result = FxTranslationBuilder.Build(fills, End160);

        result.Summary!.TranslationGainJpy.Should().Be(10_000m);
        result.Summary.EntryCount.Should().Be(1);
    }

    // ショートは USD 建て負債の再測定であり符号が逆: 売 10 @$100（150 円）→ 買戻し（160 円）: −1,000 × (160 − 150) = −10,000 円。
    [Fact]
    public void ショートの決済は符号が逆になる()
    {
        var fills = new[]
        {
            Us(TradeSide.Sell, 10, 100m, rate: 150m),
            Us(TradeSide.Buy, 10, 90m, rate: 160m, minutes: 10),
        };

        var result = FxTranslationBuilder.Build(fills, periodEnd: null);

        result.Summary.Should().Be(new FxTranslationSummary(-10_000m, 1));
    }

    // 反転: 元の建玉を全決済してから余りを新規建て（決済約定の単価・レート）とする。
    // 買 10 @$100（150 円）→ 売 15 @$100（155 円）: 決済 1,000×5 = +5,000。余り −5 株 $500 を 155 円で認識。
    // 期末 160 円: −500 × (160 − 155) = −2,500。合計 +2,500 円・明細 2 件。
    [Fact]
    public void 反転は全量決済と新規建てに分ける()
    {
        var fills = new[]
        {
            Us(TradeSide.Buy, 10, 100m, rate: 150m),
            Us(TradeSide.Sell, 15, 100m, rate: 155m, minutes: 10),
        };

        var result = FxTranslationBuilder.Build(fills, End160);

        result.Summary!.TranslationGainJpy.Should().Be(2_500m);
        result.Summary.EntryCount.Should().Be(2);
    }

    // 銘柄は別々に畳み込む（別銘柄の売りで他銘柄の建玉を減らさない）。
    [Fact]
    public void 銘柄ごとに畳み込む()
    {
        var fills = new[]
        {
            Us(TradeSide.Buy, 10, 100m, rate: 150m, symbol: "AAPL"),
            Us(TradeSide.Sell, 10, 100m, rate: 155m, minutes: 10, symbol: "TSLA"),
        };

        var result = FxTranslationBuilder.Build(fills, End160);

        // AAPL ロング $1,000: +10,000／TSLA ショート $1,000（155 円認識）: −1,000×(160−155) = −5,000。
        result.Summary!.TranslationGainJpy.Should().Be(5_000m);
        result.Summary.EntryCount.Should().Be(2);
    }

    // ---- 不変条件（プロパティ） ----

    // レートが等しければ必ず 0 になる（FxTranslationAggregatorTests の不変条件を畳み込み経由でも保つ）。
    [Fact]
    public void 認識時と期末のレートが等しければ必ず0()
    {
        var rng = new Random(611);
        for (var i = 0; i < 200; i++)
        {
            var rate = 100m + rng.Next(0, 10_000) / 100m;
            var fills = Enumerable.Range(0, rng.Next(1, 6))
                .Select(k => Us(rng.Next(2) == 0 ? TradeSide.Buy : TradeSide.Sell,
                    rng.Next(1, 50), rng.Next(1, 500), rate, minutes: k))
                .ToList();

            var result = FxTranslationBuilder.Build(fills, new PeriodEndFxRate(rate, new DateOnly(2026, 8, 28)));

            result.Summary.Should().NotBeNull($"case {i}");
            result.Summary!.TranslationGainJpy.Should().Be(0m, $"case {i}: レートが変わらなければ為替差損益は生じない");
        }
    }

    // ---- 供給できない条件（推定しない） ----

    // 🔴 否定形: 認識時レートが未記録の対象約定が 1 件でもあれば集計しない（部分集計は別の数値になる）。件数を返す。
    [Fact]
    public void 認識時レートが未記録の約定があれば未供給とし件数を返す()
    {
        var fills = new[]
        {
            Us(TradeSide.Buy, 10, 100m, rate: null),
            Us(TradeSide.Buy, 10, 100m, rate: 150m, minutes: 10),
            Us(TradeSide.Sell, 5, 100m, rate: null, minutes: 20),
        };

        var result = FxTranslationBuilder.Build(fills, End160);

        result.Summary.Should().BeNull();
        result.UnrecordedFillCount.Should().Be(2);
    }

    // 未記録の判定は**対象約定**に限る（日本株の未記録は数えない——そもそも円換算しない）。
    [Fact]
    public void 日本株の未記録は数えない()
    {
        var result = FxTranslationBuilder.Build([Jp(TradeSide.Buy, 100, 2_500m, rate: null)], End160);

        result.Summary.Should().NotBeNull();
        result.UnrecordedFillCount.Should().Be(0);
    }

    // 🔴 否定形: 期末に建玉が残るのに期末レートが無ければ未供給（0 円と書かない）。
    [Fact]
    public void 期末に建玉が残り期末レートが無ければ未供給()
    {
        var result = FxTranslationBuilder.Build([Us(TradeSide.Buy, 10, 100m, rate: 150m)], periodEnd: null);

        result.Summary.Should().BeNull();
        result.UnrecordedFillCount.Should().Be(0);
    }

    // 対の肯定形: 期末レートがあれば残る建玉を再測定し、期末レートと観測日を載せる。
    [Fact]
    public void 期末に残る建玉は期末レートで再測定し期末レートを載せる()
    {
        var result = FxTranslationBuilder.Build([Us(TradeSide.Buy, 10, 100m, rate: 150m)], End160);

        result.Summary.Should().Be(new FxTranslationSummary(10_000m, 1, 160m, new DateOnly(2026, 8, 28)));
    }
}
