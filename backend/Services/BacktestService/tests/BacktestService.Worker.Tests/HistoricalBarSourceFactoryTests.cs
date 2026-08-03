using AiStockTrading.Backtest.Application;
using AiStockTrading.Backtest.Worker.Composable.Adapters;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.Backtest.Worker.Tests;

// FR-15, #208, IADR-0105: 構成 Backtest:BarData:Provider による過去データ源の選択。
// 安全既定は no-op（外部へ 1 リクエストも出さない）。構成不備は起動を落とさず警告して no-op へ倒す（IADR-0068 と同形）。
public class HistoricalBarSourceFactoryTests
{
    private static readonly (string Symbol, Market Market)[] OneSymbol = [("AAPL", Market.UnitedStates)];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("none")]
    [InlineData("unknown-provider")]
    public async Task 既定と構成不備は外部へ接続しない_no_op(string? provider)
    {
        var handler = new FailIfCalledHandler();
        var source = Create(new BarDataOptions { Provider = provider }, handler);

        var load = await source.LoadBarsAsync(OneSymbol, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        source.Should().BeOfType<NoOpHistoricalBarSource>();
        handler.Called.Should().BeFalse();
        load.Bars.Should().BeEmpty();
        load.Gaps.Should().HaveCount(1); // 未取得を無音にしない（銘柄ごとに欠測として残す）
    }

    [Fact]
    public void provider_stooq_で実データ源を組み立てる()
    {
        var source = Create(new BarDataOptions { Provider = "Stooq " }, new FailIfCalledHandler());

        source.Should().BeOfType<StooqHistoricalBarSource>();
    }

    [Fact]
    public void ベースURLが不正なら_no_op_へ倒す()
    {
        // 「有効化したつもりで効いていない」を起動失敗ではなく警告＋no-op で扱う（IADR-0068）。
        var options = new BarDataOptions { Provider = "stooq", Stooq = { BaseUrl = "not-a-url" } };

        Create(options, new FailIfCalledHandler()).Should().BeOfType<NoOpHistoricalBarSource>();
    }

    [Fact]
    public void ベースURL未設定なら既定のURLを使う()
    {
        var options = new BarDataOptions { Provider = "stooq", Stooq = { BaseUrl = "  " } };

        Create(options, new FailIfCalledHandler()).Should().BeOfType<StooqHistoricalBarSource>();
    }

    private static IHistoricalBarSource Create(BarDataOptions options, HttpMessageHandler handler) =>
        HistoricalBarSourceFactory.Create(
            options, new HttpClient(handler), TimeProvider.System, NullLoggerFactory.Instance);

    private sealed class FailIfCalledHandler : HttpMessageHandler
    {
        public bool Called { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Called = true;
            throw new InvalidOperationException("安全既定では外部へ接続してはならない");
        }
    }
}
