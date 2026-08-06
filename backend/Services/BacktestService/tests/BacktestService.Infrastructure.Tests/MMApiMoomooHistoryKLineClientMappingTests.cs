using AiStockTrading.Backtest.Infrastructure.Composable.Adapters;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Backtest.Infrastructure.Tests;

// FR-15, ADR-0023 決定5, IADR-0157, #382: MMApiMoomooHistoryKLineClient の SDK 非依存な写像を単体検証する
// （実 OpenD 不使用）。protobuf の組み立てそのものは live 検証に委ねる（発注側 MMApiMoomooTradeClientMappingTests と同方針）。
//
// **数値は moomoo OpenAPI の protobuf 定義そのものである。** 変えると OpenD が別の意味で解釈するため、
// 「実装を変えたらテストも変える」で追随してはならない値である。
public class MMApiMoomooHistoryKLineClientMappingTests
{
    // ADR-0023 決定5: **前復権（RehabType_Forward = 1）**。無指定にすると株式分割を跨いで価格が不連続になり、
    // バックテストの結果が壊れる。
    [Fact]
    public void 前復権はRehabType_Forwardへ写像する_ADR0023決定5()
    {
        MMApiMoomooHistoryKLineClient.MapRehabType(MoomooKLineAdjustment.ForwardAdjusted).Should().Be(1);
        MMApiMoomooHistoryKLineClient.MapRehabType(MoomooKLineAdjustment.BackwardAdjusted).Should().Be(2);
        MMApiMoomooHistoryKLineClient.MapRehabType(MoomooKLineAdjustment.None).Should().Be(0);
    }

    // ADR-0023 決定5: 日足（KLType_Day = 2）・米国株（QotMarket_US_Security = 11）。
    [Fact]
    public void 日足と米国株の定数がOpenDの定義に一致する()
    {
        MMApiMoomooHistoryKLineClient.DayKLType.Should().Be(2);
        MMApiMoomooHistoryKLineClient.UsSecurityMarket.Should().Be(11);
    }

    // OHLCV を要求する（High=1 | Open=2 | Low=4 | Close=8 | Volume=32）。落とすと応答に載らない。
    [Fact]
    public void OHLCVのフィールド要求ビットが揃っている()
    {
        MMApiMoomooHistoryKLineClient.OhlcvFields.Should().Be(1 | 2 | 4 | 8 | 32);
    }

    [Theory]
    [InlineData("2006-07-24", 2006, 7, 24)]
    [InlineData("2024-01-04 00:00:00", 2024, 1, 4)]
    public void 日足のTimeをDateOnlyへ解釈する(string time, int year, int month, int day)
    {
        MMApiMoomooHistoryKLineClient.TryParseKLineDate(time, out var date).Should().BeTrue();
        date.Should().Be(new DateOnly(year, month, day));
    }

    // 解釈できない行は採らない（架空の日付のバーを作らない）。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("20240104")]
    [InlineData("not-a-date")]
    public void 解釈できないTimeは採らない(string? time)
    {
        MMApiMoomooHistoryKLineClient.TryParseKLineDate(time, out _).Should().BeFalse();
    }

    [Fact]
    public void 期間指定はyyyy_MM_dd書式で送る()
    {
        MMApiMoomooHistoryKLineClient.FormatDate(new DateOnly(2006, 7, 24)).Should().Be("2006-07-24");
    }
}
