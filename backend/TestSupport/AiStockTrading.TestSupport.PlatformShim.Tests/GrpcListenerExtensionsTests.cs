using AiStockTrading.TestSupport.PlatformShim.Foundation.Grpc;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// NFR, MSP:ADR-0029, MSP:ADR-0075, IADR-0284 決定 5（段 0）, IADR-0328, #584:
// east-west gRPC の h2c リスナの**構成読み取り**を固定する。
//
// 🔴 要点は「gRPC を有効にした瞬間に HTTP/1.1 のポートが消えない」こと。Kestrel は `Listen*` を 1 つでも
// 構成するとホスティング URL（`ASPNETCORE_URLS` / `ASPNETCORE_HTTP_PORTS`）を捨てるため、共通ヘルパは
// ホスティング構成から HTTP 側のアドレスを読み直して再宣言する。ここではその**読み取り**を固定し、
// 解決した値が本当に bind へ届いているかは `GrpcListenerBindingTests` がソケットで観測する
// （構成の解決と実 bind は別の主張であり、片方だけでは「そもそも待受が立っていない」と区別できない）。
public class GrpcListenerExtensionsTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    // 未設定・空・0 は「立てない」＝既存 11 サービスの起動時の振る舞いを 1 バイトも変えない。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    public void Grpc_Port_が未設定または_0_ならリスナを立てない(string? raw)
    {
        GrpcListenerExtensions.ResolveGrpcPort(Config(new() { [GrpcListenerExtensions.PortKey] = raw }))
            .Should().BeNull();
    }

    [Fact]
    public void Grpc_Port_が設定されていれば読む()
    {
        GrpcListenerExtensions.ResolveGrpcPort(Config(new() { [GrpcListenerExtensions.PortKey] = "8081" }))
            .Should().Be(8081);
    }

    // 陰性対照: 構成の綴り誤り・範囲外を黙って「立てない」へ倒さない
    //（倒すと「gRPC が来ない」が設定誤りと区別できなくなる）。
    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("70000")]
    public void Grpc_Port_が不正なら起動時に落とす(string raw)
    {
        var act = () => GrpcListenerExtensions.ResolveGrpcPort(
            Config(new() { [GrpcListenerExtensions.PortKey] = raw }));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{GrpcListenerExtensions.PortKey}*");
    }

    // ASPNETCORE_URLS（`urls`）が最優先。複数は `;` 区切り。
    [Fact]
    public void HTTP_側のアドレスは_urls_を最優先で読む()
    {
        var addresses = GrpcListenerExtensions.ResolveHttpAddresses(Config(new()
        {
            [GrpcListenerExtensions.UrlsKey] = "http://+:8080;http://127.0.0.1:5005",
            [GrpcListenerExtensions.HttpPortsKey] = "9999",
        }));

        addresses.Select(a => (a.Host, a.Port)).Should().Equal(("+", 8080), ("127.0.0.1", 5005));
    }

    // ASPNETCORE_HTTP_PORTS（.NET 8 以降のコンテナ既定）は urls が無いときに効く。
    [Fact]
    public void HTTP_側のアドレスは_urls_が無ければ_http_ports_を読む()
    {
        var addresses = GrpcListenerExtensions.ResolveHttpAddresses(Config(new()
        {
            [GrpcListenerExtensions.HttpPortsKey] = "8080;8090",
        }));

        addresses.Select(a => (a.Scheme, a.Host, a.Port)).Should().Equal(("http", "*", 8080), ("http", "*", 8090));
    }

    // どちらも無ければ Kestrel の既定（localhost:5000）を再現する（1 つも bind しない形にしない）。
    [Fact]
    public void HTTP_側のアドレスが何も無ければ_Kestrel_既定を再現する()
    {
        var addresses = GrpcListenerExtensions.ResolveHttpAddresses(Config([]));

        addresses.Should().ContainSingle().Which.ToString().Should().Be(GrpcListenerExtensions.DefaultHttpUrl);
    }

    // コンテナ（helm は `ASPNETCORE_URLS=http://+:8080`）はワイルドカード。全インタフェースのままにする
    // ＝メッシュ内の他 Pod とサイドカーから届く必要がある。
    [Theory]
    [InlineData("*")]
    [InlineData("+")]
    [InlineData("0.0.0.0")]
    [InlineData("[::]")]
    public void gRPC_の待受ホストは_HTTP_が全インタフェースならそのまま(string host)
    {
        var addresses = GrpcListenerExtensions.ResolveHttpAddresses(Config(new()
        {
            [GrpcListenerExtensions.UrlsKey] = $"http://{host}:8080",
        }));

        GrpcListenerExtensions.ResolveGrpcHost(addresses).Should().Be(host);
    }

    // 陰性対照: HTTP をループバックへ絞ったなら gRPC も追随する（h2c だけが端末の外へ開かない）。
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public void gRPC_の待受ホストは_HTTP_がループバックのみなら追随する(string host)
    {
        var addresses = GrpcListenerExtensions.ResolveHttpAddresses(Config(new()
        {
            [GrpcListenerExtensions.UrlsKey] = $"http://{host}:0",
        }));

        GrpcListenerExtensions.ResolveGrpcHost(addresses).Should().Be(host);
    }

    // ホストが食い違うときは**広い側へ倒す**（狭めると「本番で繋がらない」に化ける）。
    [Fact]
    public void gRPC_の待受ホストは_HTTP_のホストが食い違えばワイルドカードへ倒す()
    {
        var addresses = GrpcListenerExtensions.ResolveHttpAddresses(Config(new()
        {
            [GrpcListenerExtensions.UrlsKey] = "http://127.0.0.1:5005;http://*:8080",
        }));

        GrpcListenerExtensions.ResolveGrpcHost(addresses).Should().Be("*");
    }

    // 同じホストが複数並ぶだけなら食い違いではない（そのホストに従う）。
    [Fact]
    public void gRPC_の待受ホストは_同一ホストが複数並ぶだけならそのホストに従う()
    {
        var addresses = GrpcListenerExtensions.ResolveHttpAddresses(Config(new()
        {
            [GrpcListenerExtensions.UrlsKey] = "http://127.0.0.1:5005;http://127.0.0.1:5006",
        }));

        GrpcListenerExtensions.ResolveGrpcHost(addresses).Should().Be("127.0.0.1");
    }
}
