using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// FR-10, FR-17, #257, IADR-0107: 市場から通貨を導く純関数と、基準通貨（JPY）の定義。
public class MarketCurrencyTests
{
    [Theory]
    [InlineData(Market.Japan, Currency.Jpy)]
    [InlineData(Market.UnitedStates, Currency.Usd)]
    public void 市場から取引通貨を導く(Market market, Currency expected)
    {
        MarketCurrency.Of(market).Should().Be(expected);
    }

    [Fact]
    public void 基準通貨は円である()
    {
        // 計画 06_technical/05_trading-assumptions §3: 基準通貨 = JPY（円換算で統一）。
        MarketCurrency.Base.Should().Be(Currency.Jpy);
    }

    [Fact]
    public void 日本市場は基準通貨であり米国市場は基準通貨ではない()
    {
        MarketCurrency.IsBaseCurrency(Market.Japan).Should().BeTrue();
        MarketCurrency.IsBaseCurrency(Market.UnitedStates).Should().BeFalse();
    }
}

// FR-10, #257, IADR-0107: 注文意図の金額は「ローカル通貨の Notional」と「基準通貨の NotionalInBase」を区別する。
public class OrderIntentCurrencyTests
{
    private static OrderIntent Intent(decimal price, int quantity, decimal fxRateToBase) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper,
            quantity, price, PositionEffect.Open, StopLossPrice: null, FxRateToBase: fxRateToBase);

    [Fact]
    public void 換算レート既定は1で基準通貨額はローカル通貨額と一致する()
    {
        // 既定 1＝JPY 市場・既存データ（レート未記録）の現行等価を保証する。
        var intent = new OrderIntent(
            "7203", Market.Japan, TradeSide.Buy, ProductType.Cash, TradeMode.Paper, 10, 2_500m);

        intent.FxRateToBase.Should().Be(1m);
        intent.Notional.Should().Be(25_000m);
        intent.NotionalInBase.Should().Be(25_000m);
    }

    [Fact]
    public void 基準通貨額は同伴レートで換算される()
    {
        var intent = Intent(price: 336.77m, quantity: 2, fxRateToBase: 150m);

        // ローカル通貨（USD）の約定代金は執行・スリッページ用に保つ。
        intent.Notional.Should().Be(673.54m);
        // 統制判定に用いる基準通貨（円）の約定代金。
        intent.NotionalInBase.Should().Be(101_031m);
    }
}
