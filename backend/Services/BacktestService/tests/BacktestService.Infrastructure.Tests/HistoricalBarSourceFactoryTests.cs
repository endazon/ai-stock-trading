using AiStockTrading.Backtest.Application;
using AiStockTrading.Backtest.Infrastructure.Composable.Adapters;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.Backtest.Infrastructure.Tests;

// FR-15, #208, IADR-0105: 構成 Backtest:BarData:Provider による過去データ源の選択。
// 安全既定は no-op（外部へ 1 リクエストも出さない）。構成不備は起動を落とさず警告して no-op へ倒す（IADR-0068 と同形）。
//
// FR-15, ADR-0023, IADR-0156, #382: 既定 none は「設定漏れ」ではなく**差し替え先の不在**である
// （実装済みの Stooq は取得不能・回避実装は禁止／代替源 moomoo は実測済みだが採用も実装も未了）。
// 本クラスは「まだ実装していない」ことが黙って変わらないよう、既定値と未採用源の扱いを固定する。
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

    // ADR-0023, IADR-0156 決定4: 構成を一切与えない＝BarDataOptions の**既定値そのもの**で no-op であること。
    // 既定を "stooq" 等へ変える変異をここで止める（上の Theory は provider を明示的に渡すため既定値を検証しない）。
    [Fact]
    public void 構成を何も与えなければ実効providerはnone_既定で外部へ接続しない()
    {
        var options = new BarDataOptions();

        HistoricalBarSourceFactory.ResolveProvider(options).Should().Be(HistoricalBarSourceFactory.None);
        Create(options, new FailIfCalledHandler()).Should().BeOfType<NoOpHistoricalBarSource>();
    }

    // IADR-0105 決定5.1: ResolveProvider（自己申告の情報源）と Create（実際の選択）は同じ答えを返す。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("none")]
    [InlineData("unknown-provider")]
    public void 実効providerの解決は既定と構成不備でnoneを返す(string? provider)
    {
        HistoricalBarSourceFactory
            .ResolveProvider(new BarDataOptions { Provider = provider })
            .Should().Be(HistoricalBarSourceFactory.None);
    }

    // ADR-0023, IADR-0156 決定4・決定6: **わざと落ちるように置いた関門**。
    // moomoo は米国株日足 OHLC の代替源として実測済み（#342 の PoC 項目 7）だが、採用には ADR-0023 の
    // 改定裁定が要り、アダプタも未実装である（docs/blocked-tasks.md B-4）。裁定前に結線すれば本テストが落ちる。
    // 落ちたときは実装だけでなく IADR-0156・FR-15 の機能/テスト仕様書・blocked-tasks を同じ PR で追随させること。
    [Theory]
    [InlineData("moomoo")]
    [InlineData("MOOMOO")]
    public void 未採用の代替源moomooを指定してもno_opへ倒れる_ADR0023の改定裁定待ち(string provider)
    {
        var options = new BarDataOptions { Provider = provider };

        HistoricalBarSourceFactory.ResolveProvider(options).Should().Be(HistoricalBarSourceFactory.None);
        Create(options, new FailIfCalledHandler()).Should().BeOfType<NoOpHistoricalBarSource>();
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
