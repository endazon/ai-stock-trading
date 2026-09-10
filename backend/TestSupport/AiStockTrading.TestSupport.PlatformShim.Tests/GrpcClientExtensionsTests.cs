using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Grpc;
using AwesomeAssertions;
using Grpc.Core;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// NFR, MSP:ADR-0029, IADR-0051, IADR-0284 決定 5（段 0）, IADR-0328, #584:
// east-west gRPC の呼び出し側に付ける s2s 資格情報を固定する。
//
// 🔴 縮退の向きを REST と同じにする —— トークンが得られないときは**メタデータを付けずに送る**
//（→ 提供側 `UNAUTHENTICATED` → 呼び出し元の既存 fail-safe）。例外にすると REST の
// 「ヘッダ無し → 401 → 安全既定」（IADR-0051 決定 1・`ServiceTokenHandler`）と向きが変わる。
public class GrpcClientExtensionsTests
{
    private sealed class StubTokenProvider(string? token) : IServiceAccessTokenProvider
    {
        public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(token);
    }

    private static AuthInterceptorContext Context() =>
        new("http://configuration-service:8081", "aistocktrading.configuration.v1.Assumptions/Get");

    [Fact]
    public async Task トークンがあれば_Authorization_メタデータを付ける()
    {
        var metadata = new Metadata();

        await GrpcClientExtensions.ApplyServiceTokenAsync(new StubTokenProvider("t0ken"), Context(), metadata);

        metadata.GetValue("authorization").Should().Be("Bearer t0ken");
    }

    // 陰性対照: 取得不能（null / 空）はヘッダ無しで送る（例外にしない・空の Bearer も送らない）。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task トークンが得られなければメタデータを付けない(string? token)
    {
        var metadata = new Metadata();

        await GrpcClientExtensions.ApplyServiceTokenAsync(new StubTokenProvider(token), Context(), metadata);

        metadata.Should().BeEmpty();
    }

    // 平文 h2c（`http://`）のチャネルに CallCredentials を載せられること。
    // 🔴 `UnsafeUseInsecureChannelCallCredentials` を外すとここで例外になる（＝この試験が守っている）。
    [Fact]
    public void 平文_h2c_のチャネルに_s2s_資格情報を載せる()
    {
        using var channel = GrpcClientExtensions.CreateAiStockTradingChannel(
            "http://configuration-service:8081", new StubTokenProvider("t0ken"));

        channel.Target.Should().Be("configuration-service:8081");
    }

    // 陰性対照: 資格情報の供給元を欠いた呼び出しは組み立て時に落とす（黙って無認可で送らない）。
    [Fact]
    public void トークン供給元が無い呼び出しは組み立て時に落とす()
    {
        var act = () => GrpcClientExtensions.CreateAiStockTradingChannel("http://configuration-service:8081", null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
