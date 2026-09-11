using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-17, NFR, MSP:ADR-0029, MSP:ADR-0075, IADR-0284 決定 5（段 1）, IADR-0328, IADR-0331, #745 (#584):
// **トランスポートの選択**（`Configuration:Grpc` の有無で REST / gRPC）を**登録関数に対して**固定する。
//
// 🔴 切替は組み立て時に構成を読むため、**登録関数を通してしか観測できない**（基盤の実装ガイドが
// 「切替の試験は登録関数に対して置く」と明記している）。振る舞い越しの試験だけだと、既定が gRPC へ
// 倒れる退行が**実配線でしか露見しない**。
public class AssumptionsTransportSelectionTests
{
    private static ServiceProvider BuildWith(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingAssumptions(config);
        return services.BuildServiceProvider();
    }

    private static IAssumptionsSource SourceOf(ServiceProvider sp) =>
        sp.GetRequiredService<IAssumptionsProvider>().Should().BeOfType<CachedAssumptionsProvider>().Subject.Source;

    // 🔴 既定は REST。`Configuration:Grpc` を書かない限り、段 1 の変更で振る舞いは 1 つも変わらない。
    [Fact]
    public void 既定は_REST_である()
    {
        using var sp = BuildWith(new() { ["Configuration:BaseUrl"] = "http://configuration-service:8080" });

        SourceOf(sp).Should().BeOfType<HttpAssumptionsClient>();
    }

    // 陽性: gRPC の宛先が宣言されていれば生成クライアントへ切り替わる。
    [Fact]
    public void 宛先が宣言されていれば_gRPC_へ切り替わる()
    {
        using var sp = BuildWith(new()
        {
            ["Configuration:BaseUrl"] = "http://configuration-service:8080",
            ["Configuration:Grpc"] = "http://configuration-service:8081",
        });

        SourceOf(sp).Should().BeOfType<GrpcAssumptionsClient>();
    }

    // 陽性: REST の BaseUrl が無くても gRPC だけで成立する（宛先は独立して宣言できる）。
    [Fact]
    public void BaseUrl_が無くても_gRPC_だけで成立する()
    {
        using var sp = BuildWith(new() { ["Configuration:Grpc"] = "http://configuration-service:8081" });

        SourceOf(sp).Should().BeOfType<GrpcAssumptionsClient>();
    }

    // 陰性対照: 空文字・空白は「未宣言」であり REST のまま（構成の削除を空文字で表す運用を壊さない）。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空の宣言は未宣言として扱う(string? grpc)
    {
        using var sp = BuildWith(new()
        {
            ["Configuration:BaseUrl"] = "http://configuration-service:8080",
            ["Configuration:Grpc"] = grpc,
        });

        SourceOf(sp).Should().BeOfType<HttpAssumptionsClient>();
    }

    // 陰性対照: どちらも無ければ従来どおり既定プロバイダ（外部接続なし。IADR-0063 決定 6）。
    [Fact]
    public void どちらも無ければ既定プロバイダ()
    {
        using var sp = BuildWith([]);

        sp.GetRequiredService<IAssumptionsProvider>().Should().BeOfType<DefaultAssumptionsProvider>();
    }

    // 🔴 陰性対照: **宣言してあるのに使えない値は起動時に落とす**（IADR-0331 決定 4）。
    // 黙って REST へ戻ると「切り替えたつもりで切り替わっていない」が綴り誤りと区別できない。
    [Theory]
    [InlineData("configuration-service:8081")]            // scheme 無し（相対）
    [InlineData("https://configuration-service:8081")]    // メッシュ内の TLS はサイドカーが終端する
    [InlineData("not a url")]
    public void 使えない宣言は起動時に落とす(string grpc)
    {
        var act = () => BuildWith(new() { ["Configuration:Grpc"] = grpc }).Dispose();

        act.Should().Throw<InvalidOperationException>().WithMessage("*Configuration:Grpc*");
    }
}
