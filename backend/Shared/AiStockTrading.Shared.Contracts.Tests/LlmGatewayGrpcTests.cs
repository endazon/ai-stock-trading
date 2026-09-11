using AiStockTrading.Shared.Contracts.Llm;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// NFR, FR-04, IADR-0332 決定 2, #746: 輸送の切替は `LlmGateway:Grpc` の**有無**で決まる。
// 🔴 **既定は REST**。この解決が「未設定でも何か返す」方向へ壊れると、既定描画は変わらないまま
// 全サービスが gRPC を話し始める（h2c ポートが無ければ全滅する）。陽性・陰性を対で固定する。
public class LlmGatewayGrpcTests
{
    // 陽性: 絶対 URI は宛先として解決する。
    [Theory]
    [InlineData("http://llmgateway-service.microservices-platform:8081")]
    [InlineData("http://localhost:8081")]
    public void 絶対_URI_は宛先として解決する(string configured) =>
        LlmGatewayGrpc.ResolveAddress(configured).Should().NotBeNull()
            .And.Subject.As<Uri>().ToString().Should().StartWith("http://");

    // 陰性対照: 未設定・空・空白・相対・http 以外は「gRPC を使わない」＝ REST（既定）。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // 🔴 `Uri.TryCreate(..., Absolute)` はこれを **scheme が `llmgateway-service` の絶対 URI**として
    // 受理する（実測）。素通しすると `http://` 書き忘れがチャネル生成の起動時例外に化ける。
    [InlineData("llmgateway-service:8081")]
    [InlineData("/complete")]
    [InlineData("grpc://llmgateway-service:8081")]
    public void 未設定や_http_以外は_gRPC_を選ばない(string? configured) =>
        LlmGatewayGrpc.ResolveAddress(configured).Should().BeNull();

    // 🔴 不正な値で例外にしない（綴り誤りをサービスの起動不能に化けさせない。IADR-0332 決定 2 の remarks）。
    [Fact]
    public void 不正な値でも例外にしない()
    {
        var act = () => LlmGatewayGrpc.ResolveAddress("::::");

        act.Should().NotThrow();
    }

    // 設定キーは 2 サービスが引く単一情報源である（env は LlmGateway__Grpc）。
    [Fact]
    public void 設定キーは_LlmGateway_Grpc_である() =>
        LlmGatewayGrpc.AddressKey.Should().Be("LlmGateway:Grpc");
}
