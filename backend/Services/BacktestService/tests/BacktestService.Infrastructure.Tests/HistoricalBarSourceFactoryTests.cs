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
// FR-15, ADR-0023 決定5, IADR-0156, IADR-0157, #382: 既定 none は「設定漏れ」ではない。
// Stooq は取得不能（回避実装は禁止）であり、使えるのは決定5 で採用された moomoo だけである。ただし決定5 は
// 「実装側で確認を要する 2 点」を本決定の前提としており、**確認が済むまで本番のバックテストへ流さない**。
// 本クラスは **既定が安全側（none）であること**と **allow-list（未知の provider は実アダプタへ落ちない）**を固定する。
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

    // ADR-0023 決定5, IADR-0157 決定5: **本テストは PR #415 が置いた関門テストの後継である。**
    //
    // 旧テスト `未採用の代替源moomooを指定してもno_opへ倒れる_ADR0023の改定裁定待ち` は、
    // 「裁定が下りた日にわざと赤くなり、記録の追随を強制する」ために置かれていた（IADR-0156 決定4）。
    // 2026-08-06 に ADR-0023 決定5 で moomoo が採用されたため**関門は設計どおり発火し**、
    // 本 PR で「採用後の正しい姿」へ書き換えた（削除ではない。経緯は IADR-0157 決定5）。
    //
    // 固定するのは「moomoo は**明示的に構成したときだけ**使われる」ことである。既定が none であることは
    // 上の `構成を何も与えなければ実効providerはnone_既定で外部へ接続しない` が引き続き固定する。
    [Theory]
    [InlineData("moomoo")]
    [InlineData("MOOMOO")]
    [InlineData(" Moomoo ")]
    public void provider_moomoo_の明示指定で履歴K線アダプタを組み立てる_ADR0023決定5(string provider)
    {
        var options = new BarDataOptions { Provider = provider };

        HistoricalBarSourceFactory.ResolveProvider(options).Should().Be(HistoricalBarSourceFactory.Moomoo);
        Create(options, new FailIfCalledHandler(), new StubMoomooHistoryKLineClient())
            .Should().BeOfType<MoomooHistoricalBarSource>();
    }

    // IADR-0157 決定2: 実効 provider が moomoo なのに OpenD 接続が未提供なのは**配線の誤り**である。
    // 黙って no-op へ倒すと、実効構成の自己申告が moomoo を示したまま 1 本もバーを取らない
    // （IADR-0105 決定5.1「自己申告と実際の選択は一致する」が破れる）。BrokerFactory の moomoo 未提供と同じ扱い。
    [Fact]
    public void moomoo指定でOpenD接続が未提供なら停止する_誤用防止()
    {
        var act = () => Create(new BarDataOptions { Provider = "moomoo" }, new FailIfCalledHandler());

        act.Should().Throw<InvalidOperationException>().WithMessage("*OpenD*");
    }

    // IADR-0157 決定2: allow-list は「既知の provider が構成の妥当性を満たしたときだけ」実アダプタを返す。
    // 接続先が不正なら moomoo でも no-op へ倒す（Stooq のベース URL 不正と同形）。
    [Theory]
    [InlineData("", (ushort)11111)]
    [InlineData("  ", (ushort)11111)]
    [InlineData("opend", (ushort)0)]
    public void moomooはOpenDの接続先が不正なら_no_opへ倒す(string host, ushort port)
    {
        var options = new BarDataOptions
        {
            Provider = "moomoo",
            Moomoo = { OpenDHost = host, OpenDPort = port },
        };

        HistoricalBarSourceFactory.ResolveProvider(options).Should().Be(HistoricalBarSourceFactory.None);
        Create(options, new FailIfCalledHandler(), new StubMoomooHistoryKLineClient())
            .Should().BeOfType<NoOpHistoricalBarSource>();
    }

    private static IHistoricalBarSource Create(
        BarDataOptions options,
        HttpMessageHandler handler,
        IMoomooHistoryKLineClient? moomooClient = null) =>
        HistoricalBarSourceFactory.Create(
            options, new HttpClient(handler), TimeProvider.System, NullLoggerFactory.Instance, moomooClient);

    // OpenD へは接続しない（本テストは選択規則だけを見る）。呼ばれたら安全既定が壊れている。
    private sealed class StubMoomooHistoryKLineClient : IMoomooHistoryKLineClient
    {
        public Task<MoomooHistoryKLinePage> RequestUsDailyKLinesAsync(
            MoomooHistoryKLineRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("選択規則のテストで OpenD を叩いてはならない");
    }

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
