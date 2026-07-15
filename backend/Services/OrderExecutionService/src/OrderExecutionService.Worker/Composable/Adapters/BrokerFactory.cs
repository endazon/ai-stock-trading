using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;

namespace AiStockTrading.OrderExecution.Worker.Composable.Adapters;

// FR-05, ADR-0002, IADR-0016: 構成 Broker:Provider によるブローカ選択。安全既定はペーパー（実弾を撃たない）。
// moomoo は OpenD（IMoomooTradeClient）経由・SIMULATE 限定で発注する（#13）。実弾（TrdEnv_Real）は撃たない。
// Provider=moomoo かつ client 未提供（OpenD 接続未構成）は起動時に停止する（誤用防止）。
internal static class BrokerFactory
{
    public const string Paper = "paper";
    public const string Moomoo = "moomoo";

    public static string Normalize(string? provider) =>
        string.IsNullOrWhiteSpace(provider) ? Paper : provider.Trim().ToLowerInvariant();

    public static bool IsMoomoo(string? provider) => Normalize(provider) == Moomoo;

    public static IBrokerAdapter Create(string? provider, IMoomooTradeClient? moomooClient = null)
    {
        return Normalize(provider) switch
        {
            Paper => new PaperBrokerAdapter(),
            Moomoo => moomooClient is not null
                ? new MoomooBrokerAdapter(moomooClient)
                : throw new InvalidOperationException(
                    "Broker:Provider=moomoo には OpenD 接続（IMoomooTradeClient）が必要です。OpenD の常駐と接続構成"
                    + "（Broker:Moomoo:OpenD:Host/Port）を確認してください。実弾は撃ちません（SIMULATE 限定・IADR-0016）。"),
            _ => throw new InvalidOperationException(
                $"未知の Broker:Provider '{provider}'。'paper' または 'moomoo' を使用してください（IADR-0016）。"),
        };
    }
}
