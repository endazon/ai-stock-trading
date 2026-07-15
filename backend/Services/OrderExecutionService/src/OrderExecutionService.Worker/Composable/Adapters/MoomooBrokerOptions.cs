using Microsoft.Extensions.Configuration;

namespace AiStockTrading.OrderExecution.Worker.Composable.Adapters;

// #13, ADR-0002: moomoo アダプタ（OpenD 接続）の構成。既定は常駐 OpenD の k8s Service（opend:11111）。
// SIMULATE 固定（実弾は撃たない・IADR-0016）。実弾解禁は別 IADR＋明示 config で。
internal sealed record MoomooBrokerOptions(string OpenDHost, ushort OpenDPort)
{
    public static MoomooBrokerOptions FromConfiguration(IConfiguration config)
    {
        var host = config["Broker:Moomoo:OpenD:Host"];
        var portStr = config["Broker:Moomoo:OpenD:Port"];
        return new MoomooBrokerOptions(
            OpenDHost: string.IsNullOrWhiteSpace(host) ? "opend" : host,
            OpenDPort: ushort.TryParse(portStr, out var p) ? p : (ushort)11111);
    }
}
