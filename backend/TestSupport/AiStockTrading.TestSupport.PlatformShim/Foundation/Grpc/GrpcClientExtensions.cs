using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using Grpc.Core;
using Grpc.Net.Client;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Grpc;

// NFR, MSP:ADR-0029, MSP:ADR-0075, IADR-0284 決定 5（段 0）, IADR-0051, IADR-0328, #584:
// east-west gRPC の呼び出し側の共通部品。基盤の
// `Platform.Shared.Infrastructure/Foundation/Grpc/GrpcClientExtensions.cs`（MSP:IADR-0379 決定 4）と
// 同じ形へ揃える（実装ガイド `docs/api/east-west-grpc.md` §4）。
//
// 🔴 平文チャネルに CallCredentials を付けるには `UnsafeUseInsecureChannelCallCredentials` が要る
// （既定では TLS 無しのチャネルでトークンを送らない）。メッシュ内の TLS はサイドカーが終端するので、
// ここで許すのは**アプリから見た平文**であり、線上は mTLS である。
//
// 🔴 トークン基盤は新設しない。REST の s2s（`AddAiStockTradingServiceToken` ＝ `ServiceTokenHandler`）と
// 同じ `IServiceAccessTokenProvider`（IADR-0051）を再利用する。取得不能時に `null` を返す契約もそのままで、
// **メタデータを付けずに送る → 提供側が `UNAUTHENTICATED` → 呼び出し元の既存 fail-safe** に倒れる
// （REST の「ヘッダ無し → 401 → 安全既定」と同じ向き。トランスポートを変えても縮退の向きを変えない）。
//
// キャッシュ・タイムアウト・リトライ・fail-safe は呼び出し元サービスの `Infrastructure` に置く
// （MSP:ADR-0029 の 2026-08-04 追記）。ここには資格情報の付け方だけを置く。
public static class GrpcClientExtensions
{
    public static CallCredentials CreateServiceCallCredentials(IServiceAccessTokenProvider tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);

        return CallCredentials.FromInterceptor((context, metadata) =>
            ApplyServiceTokenAsync(tokenProvider, context, metadata));
    }

    // インターセプタの中身を単体で観測できるように切り出す（`CallCredentials` は外から起動できない）。
    internal static async Task ApplyServiceTokenAsync(
        IServiceAccessTokenProvider tokenProvider, AuthInterceptorContext context, Metadata metadata)
    {
        var token = await tokenProvider.GetTokenAsync(context.CancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(token))
            metadata.Add("Authorization", $"Bearer {token}");
    }

    // 平文 h2c（`http://<service>:<grpcPort>`）のチャネルに s2s トークンを付けて返す。
    public static GrpcChannel CreateAiStockTradingChannel(string address, IServiceAccessTokenProvider tokenProvider) =>
        GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            Credentials = ChannelCredentials.Create(
                ChannelCredentials.Insecure, CreateServiceCallCredentials(tokenProvider)),
            UnsafeUseInsecureChannelCallCredentials = true,
        });
}
