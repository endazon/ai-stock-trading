using AiStockTrading.Backtest.Application;
using AiStockTrading.Backtest.Worker.Composable.Adapters;
using AiStockTrading.Backtest.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.Backtest.Worker.Tests;

// FR-15, ADR-0004, #208, IADR-0105: Stooq 日足 CSV の解析（純関数）。
// 実 Stooq は叩かない。解析はロケール非依存（InvariantCulture）で、破損データは部分採用せず銘柄丸ごと失敗させる。
public class StooqDailyCsvParserTests
{
    private const string ValidCsv = """
        Date,Open,High,Low,Close,Volume
        2024-01-04,182.15,183.09,180.88,181.91,71983570
        2024-01-05,181.99,182.76,180.17,181.18,62303250
        """;

    [Fact]
    public void 日足CSVをバーへ解析する()
    {
        var ok = StooqDailyCsvParser.TryParse(ValidCsv, "AAPL", Market.UnitedStates, out var bars, out var reason);

        ok.Should().BeTrue(reason);
        bars.Should().HaveCount(2);
        bars[0].Should().Be(new PriceBar(
            "AAPL", Market.UnitedStates, new DateOnly(2024, 1, 4), 182.15m, 183.09m, 180.88m, 181.91m, 71_983_570));
        bars[1].Date.Should().Be(new DateOnly(2024, 1, 5));
    }

    [Fact]
    public void 解析結果は日付昇順で返す()
    {
        const string unordered = """
            Date,Open,High,Low,Close,Volume
            2024-01-05,181.99,182.76,180.17,181.18,62303250
            2024-01-04,182.15,183.09,180.88,181.91,71983570
            """;

        var ok = StooqDailyCsvParser.TryParse(unordered, "AAPL", Market.UnitedStates, out var bars, out _);

        ok.Should().BeTrue();
        bars.Select(b => b.Date).Should().BeInAscendingOrder();
    }

    [Fact]
    public void CRLF改行と末尾空行を許容する()
    {
        var csv = ValidCsv.Replace("\n", "\r\n") + "\r\n\r\n";

        var ok = StooqDailyCsvParser.TryParse(csv, "AAPL", Market.UnitedStates, out var bars, out _);

        ok.Should().BeTrue();
        bars.Should().HaveCount(2);
    }

    [Fact]
    public void 出来高が空の行は0として扱う()
    {
        // 指数・一部銘柄は出来高列が空になる。欠損は 0 として通す（価格の破損とは別扱い）。
        const string csv = """
            Date,Open,High,Low,Close,Volume
            2024-01-04,182.15,183.09,180.88,181.91,
            """;

        var ok = StooqDailyCsvParser.TryParse(csv, "AAPL", Market.UnitedStates, out var bars, out _);

        ok.Should().BeTrue();
        bars[0].Volume.Should().Be(0);
    }

    [Fact]
    public void 始値終値が高値安値の境界と一致する行は受け入れる()
    {
        // 寄り天・寄り底（Open==High / Close==Low 等）は正常なデータ。境界を破損として弾かない。
        const string csv = """
            Date,Open,High,Low,Close,Volume
            2024-01-04,186.06,186.06,182.73,182.73,100
            """;

        StooqDailyCsvParser.TryParse(csv, "AAPL", Market.UnitedStates, out var bars, out _).Should().BeTrue();
        bars.Should().ContainSingle();
    }

    [Fact]
    public void データなし応答は解析失敗として扱う()
    {
        var ok = StooqDailyCsvParser.TryParse("No data", "AAPL", Market.UnitedStates, out var bars, out var reason);

        ok.Should().BeFalse();
        bars.Should().BeEmpty();
        reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void 空応答は解析失敗として扱う()
    {
        StooqDailyCsvParser.TryParse("   ", "AAPL", Market.UnitedStates, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void ヘッダのみでデータ行が無ければ解析失敗として扱う()
    {
        // 期間指定が空振りした場合。「0 本のバーで合格」を作らないため欠測として扱う。
        StooqDailyCsvParser.TryParse("Date,Open,High,Low,Close,Volume", "AAPL", Market.UnitedStates, out _, out _)
            .Should().BeFalse();
    }

    // FR-15, #208, IADR-0109: 2026-07-28 時点の Stooq は、プログラムからの取得に対して CSV ではなく
    // JavaScript の proof-of-work によるボット検知チャレンジ（HTTP 200）を返す。実際の応答で確認済み。
    // チャレンジの回避は行わない方針のため、この応答は**欠測**として扱われなければならない
    //（価格 0 本のまま合格判定へ進ませない）。
    [Fact]
    public void ボット検知チャレンジ応答は解析失敗として扱う()
    {
        const string challenge =
            """<!DOCTYPE html><html><head><meta charset="utf-8"></head><body><noscript>This site requires JavaScript to verify your browser.</noscript><script>/* proof-of-work */</script></body></html>""";

        var ok = StooqDailyCsvParser.TryParse(challenge, "AAPL", Market.UnitedStates, out var bars, out var reason);

        ok.Should().BeFalse();
        bars.Should().BeEmpty();
        reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void 想定外のヘッダは解析失敗として扱う()
    {
        const string csv = """
            <html><body>rate limited</body></html>
            """;

        StooqDailyCsvParser.TryParse(csv, "AAPL", Market.UnitedStates, out _, out _).Should().BeFalse();
    }

    [Theory]
    // 日付不正 / 数値不正 / 列不足 / 価格 0 以下 / 高値 < 安値 / 出来高負値
    [InlineData("2024-13-45,1,2,0.5,1.5,100")]
    [InlineData("2024-01-04,abc,2,0.5,1.5,100")]
    [InlineData("2024-01-04,1,2,0.5")]
    [InlineData("2024-01-04,1,2,0,1.5,100")]
    [InlineData("2024-01-04,1,0.4,0.5,0.45,100")]
    [InlineData("2024-01-04,1,2,0.5,1.5,-1")]
    // 始値・終値が [安値, 高値] の外（OHLC の整合が壊れている）
    [InlineData("2024-01-04,2.5,2,0.5,1.5,100")]
    [InlineData("2024-01-04,1,2,0.5,0.4,100")]
    [InlineData("2024-01-04,0.4,2,0.5,1.5,100")]
    [InlineData("2024-01-04,1,2,0.5,2.5,100")]
    public void 不正な行があれば部分採用せず解析失敗とする(string badRow)
    {
        // 部分採用は偽の価格ギャップを作り、約定不能・誤った DD を生む。銘柄丸ごと欠測にする。
        var csv = $"""
            Date,Open,High,Low,Close,Volume
            2024-01-04,182.15,183.09,180.88,181.91,71983570
            {badRow}
            """;

        var ok = StooqDailyCsvParser.TryParse(csv, "AAPL", Market.UnitedStates, out var bars, out var reason);

        ok.Should().BeFalse();
        bars.Should().BeEmpty();
        reason.Should().NotBeNullOrWhiteSpace();
    }
}
