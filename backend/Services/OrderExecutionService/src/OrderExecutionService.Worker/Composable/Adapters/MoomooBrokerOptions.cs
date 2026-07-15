using Microsoft.Extensions.Configuration;

namespace AiStockTrading.OrderExecution.Worker.Composable.Adapters;

// #13, ADR-0002: moomoo アダプタ（OpenD 接続）の構成。既定は常駐 OpenD の k8s Service（opend:11111）。
// SIMULATE 固定（実弾は撃たない・IADR-0016）。実弾解禁は別 IADR＋明示 config で。
//
// RsaPrivateKeyPath: OpenD の暗号化通信用 RSA 秘密鍵（PKCS#1・PEM）のファイルパス。
// moomoo は cross-network（別 Pod 間）の trade 接続に暗号化を要求するため、in-cluster では必須。
// OpenD 側の <rsa_private_key> と同一鍵を指す（k8s Secret をマウント）。未設定なら非暗号（loopback 用）。
internal sealed record MoomooBrokerOptions(string OpenDHost, ushort OpenDPort, string? RsaPrivateKeyPath = null)
{
    public static MoomooBrokerOptions FromConfiguration(IConfiguration config)
    {
        var host = config["Broker:Moomoo:OpenD:Host"];
        var portStr = config["Broker:Moomoo:OpenD:Port"];
        var rsaPath = config["Broker:Moomoo:OpenD:RsaPrivateKeyPath"];
        return new MoomooBrokerOptions(
            OpenDHost: string.IsNullOrWhiteSpace(host) ? "opend" : host,
            OpenDPort: ushort.TryParse(portStr, out var p) ? p : (ushort)11111,
            RsaPrivateKeyPath: string.IsNullOrWhiteSpace(rsaPath) ? null : rsaPath);
    }
}
