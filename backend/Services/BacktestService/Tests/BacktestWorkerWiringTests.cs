using System.Net;
using System.Net.Http.Json;
using BacktestService.Features.Backtest;
using BacktestService.Infrastructure.ExternalServices;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BacktestService.Tests;

// FR-15, #208, IADR-0105: ホストの配線を固定する。
// 「Program.cs の配線が外れていないこと」だけを見る（挙動は各アダプタの単体テストが見る）。配線が外れると、
// 構成で有効化したつもりの実過去データ源が黙って no-op のままになり、Stage 0 が永久に不合格になる。
public class BacktestWorkerWiringTests
{
    [Fact]
    public void 既定構成では外部へ接続しないno_opが解決される_failsafe()
    {
        using var factory = new BacktestWorkerWebApplicationFactory();

        factory.Services.GetRequiredService<IHistoricalBarSource>()
            .Should().BeOfType<NoOpHistoricalBarSource>();
    }

    [Fact]
    public void provider_stooq_の指定で実データ源が解決される()
    {
        using var factory = new BacktestWorkerWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Backtest:BarData:Provider"] = "stooq",
        });

        factory.Services.GetRequiredService<IHistoricalBarSource>()
            .Should().BeOfType<StooqHistoricalBarSource>();
    }

    // FR-15, ADR-0023 決定5, IADR-0157: provider=moomoo の明示指定でだけ履歴 K 線アダプタが解決される。
    // OpenD への接続は遅延（初回取得時）であり、解決の時点では 1 本も張らない。
    [Fact]
    public void provider_moomoo_の指定で履歴K線アダプタが解決される_ADR0023決定5()
    {
        using var factory = new BacktestWorkerWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Backtest:BarData:Provider"] = "moomoo",
        });

        factory.Services.GetRequiredService<IHistoricalBarSource>()
            .Should().BeOfType<MoomooHistoricalBarSource>();
    }

    // ADR-0023 決定5, IADR-0157 決定2: OpenD の接続先が不正なら moomoo でも no-op へ倒し、自己申告も一致させる。
    [Fact]
    public async Task moomooのOpenD接続先が不正なら自己申告もno_opを示す()
    {
        using var factory = new BacktestWorkerWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Backtest:BarData:Provider"] = "moomoo",
            ["Backtest:BarData:Moomoo:OpenDPort"] = "0",
        });

        factory.Services.GetRequiredService<IHistoricalBarSource>().Should().BeOfType<NoOpHistoricalBarSource>();

        var dto = await factory.CreateClient()
            .GetFromJsonAsync<ServiceIntrospectionDto>(IntrospectionExtensions.IntrospectionPath);

        dto!.Ports.Should().ContainSingle(p => p.Port == "historical-bar-data" && p.Implementation == "none");
    }

    [Fact]
    public void 未知のproviderでもホストは起動しno_opへ倒れる()
    {
        // 構成不備で起動を落とさない（バーが取れなければ Stage 0 が不合格になる＝安全側に縮退する）。
        using var factory = new BacktestWorkerWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Backtest:BarData:Provider"] = "no-such-provider",
        });

        factory.Services.GetRequiredService<IHistoricalBarSource>()
            .Should().BeOfType<NoOpHistoricalBarSource>();
    }

    [Fact]
    public void 過去データ源は単一インスタンスとして解決される()
    {
        // レート予算（トークンバケット）はインスタンス単位。都度生成されると自制が効かなくなる。
        using var factory = new BacktestWorkerWebApplicationFactory();

        factory.Services.GetRequiredService<IHistoricalBarSource>()
            .Should().BeSameAs(factory.Services.GetRequiredService<IHistoricalBarSource>());
    }

    [Fact]
    public async Task 実効構成の自己申告に選択中の過去データ源を載せる()
    {
        // #22 受け入れ基準③: 「有効化したつもりで効いていない」をメッシュ内部から確認できるようにする。
        using var factory = new BacktestWorkerWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Backtest:BarData:Provider"] = "stooq",
        });

        var dto = await factory.CreateClient()
            .GetFromJsonAsync<ServiceIntrospectionDto>(IntrospectionExtensions.IntrospectionPath);

        dto.Should().NotBeNull();
        dto!.Service.Should().Be("backtest-service");
        dto.Ports.Should().ContainSingle(p => p.Port == "historical-bar-data" && p.Implementation == "stooq");
    }

    [Fact]
    public async Task ベースURLが不正なら自己申告もno_opを示す()
    {
        // 自己申告と実際の選択がずれると、構成不備の検知そのものが嘘になる（選択規則は ResolveProvider が単一情報源）。
        using var factory = new BacktestWorkerWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Backtest:BarData:Provider"] = "stooq",
            ["Backtest:BarData:Stooq:BaseUrl"] = "not-a-url",
        });

        factory.Services.GetRequiredService<IHistoricalBarSource>().Should().BeOfType<NoOpHistoricalBarSource>();

        var dto = await factory.CreateClient()
            .GetFromJsonAsync<ServiceIntrospectionDto>(IntrospectionExtensions.IntrospectionPath);

        dto!.Ports.Should().ContainSingle(p => p.Port == "historical-bar-data" && p.Implementation == "none");
    }

    [Fact]
    public async Task ヘルスチェックは起動直後にreadyを返す_DBもバスも持たない()
    {
        using var factory = new BacktestWorkerWebApplicationFactory();

        var response = await factory.CreateClient().GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

// FR-15, ADR-0023 決定5, IADR-0060 決定5, IADR-0157, #382: **構成不備が「起動時」に落ちることを固定する。**
//
// `MoomooBarDataPreflight` をコンストラクタへ置くだけでは起動時に効かない —— `AddSingleton<T>(factory)` は
// 遅延生成であり、BacktestService には発注経路の `BrokerAvailabilityProbeService` にあたる eager な
// 消費者が無いためである。`Program.cs` が `builder.Build()` の直後に強制解決している。
//
// **本テストが守るのは「例外が出ること」ではなく「ホストの起動そのものが失敗すること」である。**
// 起動が通ってしまうと、鍵のマウント漏れは初回のバー取得まで顕在化しない（＝preflight の意味が消える）。
public class BacktestWorkerStartupPreflightTests
{
    [Fact]
    public void provider_moomooで鍵パスが設定済みでもファイルが無ければホストの起動が失敗する()
    {
        using var factory = new BacktestWorkerWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Backtest:BarData:Provider"] = "moomoo",
            ["Backtest:BarData:Moomoo:RsaPrivateKeyPath"] = "/secrets/moomoo-rsa/does-not-exist.pem",
        });

        // Services へ触れた時点でホストが構築される（＝Program.cs の強制解決が走る）。
        var act = () => _ = factory.Services;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*RsaPrivateKeyPath*");
    }

    // **否定形**: 既定（provider 未指定）では moomoo の構成を一切見ない。
    // 見てしまうと、moomoo を使わない環境が moomoo の構成不備で起動できなくなる。
    [Fact]
    public void 既定構成では鍵パスが不正でも起動する_moomooを使わない環境を巻き込まない()
    {
        using var factory = new BacktestWorkerWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Backtest:BarData:Moomoo:RsaPrivateKeyPath"] = "/secrets/moomoo-rsa/does-not-exist.pem",
        });

        factory.Services.GetRequiredService<IHistoricalBarSource>()
            .Should().BeOfType<NoOpHistoricalBarSource>();
    }
}
