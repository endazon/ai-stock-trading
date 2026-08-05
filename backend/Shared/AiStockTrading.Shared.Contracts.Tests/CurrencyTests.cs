using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// FR-10, FR-17, #257, #364, IADR-0107/0152: 市場から通貨を導く純関数と、基準通貨（USD）の定義。
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
    public void 基準通貨は米ドルである()
    {
        // 計画 06_technical/05_trading-assumptions §3: 基準通貨（判定）= USD（利用者決定 2026-07-31）。
        // #364, IADR-0152 決定1: 本定数が基準通貨の単一情報源である。
        MarketCurrency.Base.Should().Be(Currency.Usd);
    }

    [Fact]
    public void 米国市場は基準通貨であり日本市場は基準通貨ではない()
    {
        MarketCurrency.IsBaseCurrency(Market.UnitedStates).Should().BeTrue();
        MarketCurrency.IsBaseCurrency(Market.Japan).Should().BeFalse();
    }
}

// FR-16, FR-10, #364, IADR-0152 決定6: 金額表記の単位（通貨コード・補助単位の桁数）。
public class CurrencyFormatTests
{
    [Theory]
    [InlineData(Currency.Jpy, "JPY")]
    [InlineData(Currency.Usd, "USD")]
    public void 通貨の表示コードを返す(Currency currency, string expected)
    {
        CurrencyFormat.CodeOf(currency).Should().Be(expected);
    }

    [Theory]
    [InlineData(Currency.Jpy, 0)]
    [InlineData(Currency.Usd, 2)]
    public void 補助単位の桁数を返す(Currency currency, int expected)
    {
        CurrencyFormat.MinorUnitDigits(currency).Should().Be(expected);
    }

    // **否定形**: 未定義の通貨を既定値へ倒さない（単位が黙って誤ることを許さない）。
    [Fact]
    public void 未定義の通貨は既定値へ倒さず落ちる()
    {
        var undefined = (Currency)999;

        FluentActions.Invoking(() => CurrencyFormat.CodeOf(undefined))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => CurrencyFormat.MinorUnitDigits(undefined))
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}

// FR-10, #257, #364, IADR-0107/0152: 注文意図の金額は「ローカル通貨の Notional」と「基準通貨の NotionalInBase」を区別する。
public class OrderIntentCurrencyTests
{
    private static OrderIntent Intent(decimal price, int quantity, decimal fxRateToBase) =>
        new("7203", Market.Japan, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
            quantity, price, PositionEffect.Open, StopLossPrice: null, FxRateToBase: fxRateToBase);

    [Fact]
    public void 換算レート既定は1で基準通貨額はローカル通貨額と一致する()
    {
        // #364, IADR-0152 決定1: 既定 1＝基準通貨市場（米国株）。基準通貨の反転で既定の意味も反転する。
        var intent = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 2, 336.77m);

        intent.FxRateToBase.Should().Be(1m);
        intent.Notional.Should().Be(673.54m);
        intent.NotionalInBase.Should().Be(673.54m);
    }

    [Fact]
    public void 基準通貨額は同伴レートで換算される()
    {
        // 日本株（ローカル通貨 JPY）を基準通貨（USD）へ換算する。レートは「JPY 1 単位あたりの USD 額」。
        var intent = Intent(price: 2_500m, quantity: 10, fxRateToBase: 0.01m);

        // ローカル通貨（JPY）の約定代金は執行・スリッページ用に保つ。
        intent.Notional.Should().Be(25_000m);
        // 統制判定に用いる基準通貨（USD）の約定代金。
        intent.NotionalInBase.Should().Be(250m);
    }
}
